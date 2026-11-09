# CorePilot

**Moderní Dynamic Island pro Windows 10 a Windows 11.**

CorePilot spojuje hudbu, hlasitost, schránku, indikátory kláves a malý přehrávač v jednotném rozhraní nad pracovní plochou.

## Funkce
- **Hudba:** obal alba, informace o skladbě, přehrát/pauza, další/předchozí a posun skladby u podporovaných přehrávačů
- **Hlasitost:** posuvník pro systémovou hlasitost a ztlumení
- **Schránka:** volitelná historie textu a obrázků, vyvolání a smazání
- **Caps Lock / Num Lock:** nenápadná oznámení
- **Mini přehrávač:** plovoucí widget nad hlavním panelem Windows
- **Vzhled:** světlý/tmavý režim, animace a vypínání modulů

## Instalace a testování

Zatím jde o **zdrojový prototyp**, nikoli ověřené finální vydání. Vyžaduje Windows 10 (2004+) nebo Windows 11 a .NET 10 SDK.

```powershell
dotnet restore CorePilot.slnx
dotnet build CorePilot.slnx -c Release
dotnet run --project tests/CorePilot.Tests/CorePilot.Tests.csproj -c Release
dotnet run --project src/CorePilot/CorePilot.csproj -c Release
```

Schránka je vypnutá, dokud ji nepovolíš. Data se uchovávají pouze v paměti aplikace, neodesílají se na server.

**Omezení:** vlastní Mica/Acrylic efekty zatím nejsou dokončené; mini přehrávač je nad taskbarem, ne uvnitř; Windows může zobrazit vlastní oznámení hlasitosti současně s CorePilotem. Aktuální README v angličtině obsahuje všechny podrobnosti.

Starší GitHub Releases se mohou vztahovat k předchozí aplikaci na tvorbu USB a nejsou vydáním této verze CorePilotu.
