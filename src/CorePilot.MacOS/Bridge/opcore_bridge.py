#!/usr/bin/env python3
import argparse
import copy
import importlib.util
import json
import os
import subprocess
import sys
import traceback


def load_json(path):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def write_json(path, data):
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(data, handle, indent=2, ensure_ascii=False)


def load_upstream(upstream):
    entry = os.path.join(upstream, "OpCore-Simplify.py")
    if not os.path.isfile(entry):
        raise RuntimeError("Pinned OpCore Simplify entry point was not found.")

    if upstream not in sys.path:
        sys.path.insert(0, upstream)

    spec = importlib.util.spec_from_file_location("corepilot_opcore_simplify", entry)
    if spec is None or spec.loader is None:
        raise RuntimeError("Unable to load OpCore Simplify module.")

    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class StrictPolicyResponder:
    def __init__(self, profile):
        self.profile = profile

    def answer(self, prompt="Press Enter to continue..."):
        text = (prompt or "").strip()
        lower = text.lower()

        if not text or lower.startswith("press enter"):
            return ""

        if "select audio kext for your system" in lower:
            # Tahoe policy deliberately avoids silently enabling OCLP/root patches.
            if "deferred" in self.profile.get("AudioMode", "").lower():
                return "2"
            return "1"

        if "select kext for your amd" in lower and "gpu" in lower:
            # CorePilot default for Navi 21/23 is WhateverGreen.
            return "2"

        if "select kext for your intel wifi device" in lower:
            wifi = self.profile.get("WifiMode", "").lower()
            return "2" if wifi.startswith("itlwm") else "1"

        if "enter the id of the codec layout" in lower:
            # Empty input selects the upstream default layout-id.
            return ""

        raise RuntimeError(
            "Unexpected interactive upstream prompt. CorePilot stopped safely: " + text
        )


def find_named_file(root, wanted_name):
    candidates = []
    wanted = wanted_name.lower()
    for current, _dirs, files in os.walk(root):
        for name in files:
            if name.lower() == wanted:
                candidates.append(os.path.join(current, name))
    return candidates[0] if candidates else None


def find_ocvalidate(root):
    return find_named_file(root, "ocvalidate.exe")


def validate_target_with_upstream(utils, target, native_range, oclp_range):
    parse = utils.parse_darwin_version
    if native_range and parse(native_range[0]) <= parse(target) <= parse(native_range[-1]):
        return "native"
    if oclp_range and parse(oclp_range[-1]) <= parse(target) <= parse(oclp_range[0]):
        return "oclp"
    raise RuntimeError(
        "Selected macOS target is outside the compatibility range reported by OpCore Simplify."
    )


def run_self_test():
    profile = {
        "AudioMode": "Deferred on Tahoe",
        "WifiMode": "itlwm + HeliPort"
    }
    responder = StrictPolicyResponder(profile)

    assert responder.answer("Press Enter to continue...") == ""
    assert responder.answer("Select audio kext for your system: ") == "2"
    assert responder.answer("Select kext for your AMD Navi 21 GPU (default: WhateverGreen): ") == "2"
    assert responder.answer("Select kext for your Intel WiFi device (default: itlwm): ") == "2"
    assert responder.answer("Enter the ID of the codec layout you want to use (default: 7): ") == ""

    try:
        responder.answer("Unknown future upstream question?")
    except RuntimeError:
        pass
    else:
        raise AssertionError("Unknown upstream prompts must fail closed.")

    print("CorePilot OpCore bridge self-test OK")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream")
    parser.add_argument("--workspace")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        run_self_test()
        return 0

    if not args.upstream or not args.workspace:
        parser.error("--upstream and --workspace are required unless --self-test is used")

    workspace = os.path.abspath(args.workspace)
    upstream = os.path.abspath(args.upstream)
    result_path = os.path.join(workspace, "CorePilotEfiBuild.json")
    log_path = os.path.join(workspace, "CorePilotEfiBuild.log")

    result = {
        "Success": False,
        "EfiDirectory": "",
        "ConfigPath": "",
        "SmbiosModel": "",
        "Kexts": [],
        "AcpiPatches": [],
        "DisabledDevices": [],
        "NeedsOclp": False,
        "OcValidateStatus": "not-run",
        "OcValidateOutput": "",
        "MacRecoveryPath": "",
        "Error": ""
    }

    try:
        profile_path = os.path.join(workspace, "CorePilotAutomationProfile.json")
        report_path = os.path.join(workspace, "SysReport", "Report.json")
        acpi_path = os.path.join(workspace, "SysReport", "ACPI")

        profile = load_json(profile_path)
        if profile.get("RequiresReview"):
            raise RuntimeError("Automation profile still requires Advanced review.")
        if not profile.get("CanBuildEfi"):
            raise RuntimeError("Automation profile blocks EFI generation.")

        upstream_module = load_upstream(upstream)

        # Patch upstream input centrally. Only known policy prompts are accepted;
        # any new prompt fails closed instead of guessing.
        from Scripts import utils as upstream_utils
        policy_responder = StrictPolicyResponder(profile)

        def corepilot_request_input(_utils_instance, prompt="Press Enter to continue..."):
            return policy_responder.answer(prompt)

        upstream_utils.Utils.request_input = corepilot_request_input

        ocpe = upstream_module.OCPE()
        ocpe.result_dir = os.path.join(workspace, "Results")

        valid, errors, warnings, report = ocpe.v.validate_report(report_path)
        if not valid or errors:
            raise RuntimeError(
                "Hardware Sniffer report validation failed: " + "; ".join(errors or [])
            )

        ocpe.ac.read_acpi_tables(acpi_path)

        report, native_range, oclp_range = ocpe.c.check_compatibility(report)
        target = profile["DarwinVersion"]
        target_mode = validate_target_with_upstream(
            ocpe.u, target, native_range, oclp_range
        )

        customized, disabled_devices, needs_oclp = ocpe.h.hardware_customization(
            report, target
        )

        if target_mode == "oclp":
            needs_oclp = True

        if needs_oclp:
            raise RuntimeError(
                "This configuration requires OpenCore Legacy Patcher. "
                "CorePilot will not enable OCLP automatically without an explicit Advanced approval."
            )

        # Use the upstream SMBIOS selector on the full enriched hardware report.
        # The C# profile value is a preview/fallback, not an override.
        smbios_model = ocpe.s.select_smbios_model(customized, target)
        if not smbios_model:
            smbios_model = profile.get("SmbiosModel", "")
        if not smbios_model:
            raise RuntimeError("Unable to resolve an SMBIOS model.")

        ocpe.ac.select_acpi_patches(customized, disabled_devices)

        needs_oclp = ocpe.k.select_required_kexts(
            customized,
            target,
            needs_oclp,
            ocpe.ac.patches
        )

        if needs_oclp:
            raise RuntimeError(
                "Kext selection requested OCLP. CorePilot stopped before applying root-patch-dependent configuration."
            )

        ocpe.s.smbios_specific_options(
            customized,
            smbios_model,
            target,
            ocpe.ac.patches,
            ocpe.k
        )

        gathered = ocpe.o.gather_bootloader_kexts(ocpe.k.kexts, target)
        if gathered is False:
            raise RuntimeError("OpCore Simplify could not gather required OpenCore/kext files.")

        ocpe.build_opencore_efi(
            customized,
            disabled_devices,
            smbios_model,
            target,
            needs_oclp
        )

        efi_directory = os.path.join(ocpe.result_dir, "EFI")
        config_path = os.path.join(efi_directory, "OC", "config.plist")
        boot_path = os.path.join(efi_directory, "BOOT", "BOOTx64.efi")
        opencore_path = os.path.join(efi_directory, "OC", "OpenCore.efi")

        for required in (config_path, boot_path, opencore_path):
            if not os.path.isfile(required):
                raise RuntimeError("Generated EFI is incomplete: missing " + required)

        ocvalidate = find_ocvalidate(ocpe.k.ock_files_dir)
        if not ocvalidate:
            raise RuntimeError("ocvalidate.exe was not found in the downloaded OpenCorePkg.")

        validation = subprocess.run(
            [ocvalidate, config_path],
            cwd=os.path.dirname(ocvalidate),
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            check=False
        )
        validation_output = validation.stdout or ""

        if validation.returncode != 0:
            raise RuntimeError(
                "ocvalidate rejected the generated config.plist: " + validation_output.strip()
            )

        selected_kexts = sorted([
            item.name for item in ocpe.k.kexts if getattr(item, "checked", False)
        ])
        selected_acpi = sorted([
            patch.name for patch in ocpe.ac.patches if getattr(patch, "checked", False)
        ])

        macrecovery_path = find_named_file(ocpe.k.ock_files_dir, "macrecovery.py") or ""

        result.update({
            "Success": True,
            "EfiDirectory": efi_directory,
            "ConfigPath": config_path,
            "SmbiosModel": smbios_model,
            "Kexts": selected_kexts,
            "AcpiPatches": selected_acpi,
            "DisabledDevices": sorted(disabled_devices.keys()),
            "NeedsOclp": bool(needs_oclp),
            "OcValidateStatus": "success",
            "OcValidateOutput": validation_output.strip(),
            "MacRecoveryPath": macrecovery_path,
            "Error": ""
        })

        write_json(result_path, result)
        with open(log_path, "a", encoding="utf-8") as log:
            log.write("\nCorePilot EFI build completed successfully.\n")
            log.write(validation_output)
        print("COREPILOT_EFI_BUILD_OK")
        return 0

    except BaseException as exc:
        if isinstance(exc, KeyboardInterrupt):
            raise

        result["Error"] = str(exc)
        try:
            write_json(result_path, result)
            with open(log_path, "a", encoding="utf-8") as log:
                log.write("\n" + traceback.format_exc() + "\n")
        except Exception:
            pass

        traceback.print_exc()
        return 1


if __name__ == "__main__":
    sys.exit(main())
