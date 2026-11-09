using CorePilot.Core;
var checks = new (string Name, Action Check)[]
{
    ("time", () => Equal("0:09", UiText.Time(TimeSpan.FromSeconds(9)))),
    ("hour", () => Equal("1:02:03", UiText.Time(new TimeSpan(1, 2, 3)))),
    ("negative time", () => Equal("0:00", UiText.Time(TimeSpan.FromSeconds(-1)))),
    ("volume min", () => Equal(0d, UiText.PercentToScalar(-100))),
    ("volume max", () => Equal(1d, UiText.PercentToScalar(1000))),
    ("volume rounding", () => Equal(76, UiText.ScalarToPercent(.756))),
    ("seek min", () => Equal(0d, UiText.ClampSeek(-8, 120))),
    ("seek max", () => Equal(120d, UiText.ClampSeek(140, 120))),
};
int failures = 0;
foreach (var (name, check) in checks) {
    try { check(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { Console.WriteLine($"FAIL {name}: {e.Message}"); failures++; }
}
return failures == 0 ? 0 : 1;
static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}
