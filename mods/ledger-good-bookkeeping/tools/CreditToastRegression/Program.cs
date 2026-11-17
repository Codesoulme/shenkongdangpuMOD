using System.Globalization;
using System.Reflection;
using AIWorldPawnshop;
using AIWorldPawnshop.UI;
using TMPro;
using UnityEngine;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
var update = typeof(CreditChangeToastAnimation).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;
var cases = new (string Name, Action Test)[]
{
    ("single", () => { CreditChangeToast.Show(100); Drain(); Expect(100); }),
    ("1000 item sale creates one total toast", () => { Sale(() => { for (var i = 0; i < 1000; i++) CreditChangeToast.Show(10); }); Drain(); Expect(10000); }),
    ("1000 item purchase creates one total toast", () => { Order("BuyMultipleItem", () => { for (var i = 0; i < 1000; i++) CreditChangeToast.Show(-10); }); Drain(); Expect(-10000); }),
    ("1000 item wholesale creates one actual-payment toast", () => { Wholesale(10000, Enumerable.Repeat(10L, 1000).ToArray()); Drain(); Expect(-10000); }),
    ("batch purchase total", () => { Order("BuyMultipleItem", () => { CreditChangeToast.Show(-100); CreditChangeToast.Show(-250); }); Drain(); Expect(-350); }),
    ("wholesale total", () => { Wholesale(350, 100, 250); Drain(); Expect(-350); }),
    ("wholesale uses rounded payment instead of item sum", () => { Wholesale(181, 90, 90); Drain(); Expect(-181); }),
    ("equal-price wholesale total", () => { Wholesale(200, 100, 100); Drain(); Expect(-200); }),
    ("wholesale keeps unrelated completion income and expense", () =>
    {
        PlayerStore.Instance = new PlayerStore { playerCash = 1000 };
        Order("OnWholesaleClick", () =>
        {
            PlayerStore.Instance.playerCash -= 181;
            WholesaleCreditToastPatch.ObservePayment();
            WholesaleItem(90); WholesaleItem(90);
            CreditChangeToast.Show(50); CreditChangeToast.Show(-20);
        });
        Drain(); Expect(-181, 50, -20);
    }),
    ("cancelled wholesale has no toast", () => { Order("OnWholesaleClick", () => { }); Drain(); Expect(); }),
    ("partial wholesale failure releases scope and retains paid total", () =>
    {
        PlayerStore.Instance = new PlayerStore { playerCash = 1000 };
        try
        {
            Order("OnWholesaleClick", () =>
            {
                PlayerStore.Instance.playerCash -= 181;
                WholesaleCreditToastPatch.ObservePayment(); WholesaleItem(90);
                throw new InvalidOperationException("partial callback failure");
            });
        }
        catch (InvalidOperationException) { }
        CreditChangeToast.Show(-30); Drain(); Expect(-181, -30);
    }),
    ("separate wholesale orders stay separate", () => { Wholesale(180, 90, 90); Wholesale(200, 100, 100); Drain(); Expect(-180, -200); }),
    ("equal-price batch purchase total", () => { Order("BuyMultipleItem", () => { CreditChangeToast.Show(-100); CreditChangeToast.Show(-100); }); Drain(); Expect(-200); }),
    ("different prices", () => { CreditChangeToast.Show(100); CreditChangeToast.Show(250); CreditChangeToast.Show(400); Drain(); Expect(100, 250, 400); }),
    ("equal prices preserved", () => { CreditChangeToast.Show(100); CreditChangeToast.Show(100); Drain(); Expect(100, 100); }),
    ("multi-item sale total", () => { Sale(() => { CreditChangeToast.Show(100); CreditChangeToast.Show(250); CreditChangeToast.Show(400); }); Drain(); Expect(750); }),
    ("equal-price sale total", () => { Sale(() => { CreditChangeToast.Show(100); CreditChangeToast.Show(100); }); Drain(); Expect(200); }),
    ("mixed directions", () => { Sale(() => { CreditChangeToast.Show(100); CreditChangeToast.Show(-25); CreditChangeToast.Show(40); }); Drain(); Expect(100, -25, 40); }),
    ("rapid consecutive frames", () => { CreditChangeToast.Show(100); Tick(); CreditChangeToast.Show(250); Tick(); CreditChangeToast.Show(-50); Drain(); Expect(100, 250, -50); }),
    ("nested sale", () => { Sale(() => { CreditChangeToast.Show(100); Sale(() => CreditChangeToast.Show(150)); CreditChangeToast.Show(50); }); Drain(); Expect(300); }),
    ("partial failure releases batch", () =>
    {
        var failure = new InvalidOperationException("partial sale");
        try { Sale(() => { CreditChangeToast.Show(100); throw failure; }); }
        catch (InvalidOperationException ex) { if (!ReferenceEquals(ex, failure)) throw; }
        CreditChangeToast.Show(200); Drain(); Expect(100, 200);
    }),
    ("partial batch purchase only shows completed expenses", () =>
    {
        try { Order("BuyMultipleItem", () => { CreditChangeToast.Show(-100); throw new InvalidOperationException("purchase failed"); }); }
        catch (InvalidOperationException) { }
        CreditChangeToast.Show(-50); Drain(); Expect(-100, -50);
    }),
    ("no successful sale", () => { Sale(() => { }); Drain(); Expect(); }),
    ("read save clears pending", () =>
    {
        CreditChangeToast.Show(100); CreditChangeToast.Show(200);
        CreditChangeToast.Reset(); CreditChangeToast.Show(50); Drain(); Expect(100, 50);
    }),
    ("destroyed scene clears pending", () =>
    {
        CreditChangeToast.Show(100); CreditChangeToast.Show(200);
        UnityEngine.Object.Destroy(ObjectStore.ActiveToasts().Single());
        CreditChangeToast.Show(50); Drain(); Expect(100, 50);
    }),
    ("zero ignored", () => { CreditChangeToast.Show(0); Drain(); Expect(); }),
    ("unavailable canvas", () =>
    {
        ObjectStore.Created.Single(g => g.name == "Canvas").SetActive(false);
        CreditChangeToast.Show(100);
        ObjectStore.Created.Single(g => g.name == "Canvas").SetActive(true);
        CreditChangeToast.Show(50); Drain(); Expect(50);
    }),
    ("large total retains integer precision", () =>
    {
        Sale(() => { CreditChangeToast.Show(int.MaxValue); CreditChangeToast.Show(int.MaxValue); });
        Drain(); Expect(4294967294L);
    }),
    ("position distance duration preserved", () =>
    {
        CreditChangeToast.Show(100);
        var toast = ObjectStore.ActiveToasts().Single();
        if (toast.Get<RectTransform>()!.anchoredPosition.y != 125) throw new Exception("start changed");
        for (var i = 0; i < 12; i++) Tick();
        if (Math.Abs(toast.Get<RectTransform>()!.anchoredPosition.y - 200) > 0.1f) throw new Exception("midpoint changed");
        for (var i = 0; i < 12; i++) Tick();
        // Float accumulation can leave 24 x 0.1f just below 2.4f.
        if (ObjectStore.ActiveToasts().Length != 0) Tick();
        if (ObjectStore.ActiveToasts().Length != 0 || Math.Abs(toast.Get<RectTransform>()!.anchoredPosition.y - 275) > 0.1f)
            throw new Exception("end or duration changed");
    }),
};

foreach (var (name, test) in cases)
{
    CreditChangeToast.Reset(); ObjectStore.Created.Clear(); Time.frameCount = 0;
    new GameObject("Canvas").AddComponent<Canvas>();
    test();
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"{cases.Length} cases passed using production toast and batch-patch sources.");

void Tick()
{
    var active = ObjectStore.ActiveToasts();
    if (active.Length > 1) throw new Exception($"{active.Length} overlapping toasts");
    foreach (var toast in active) update.Invoke(toast.Get<CreditChangeToastAnimation>(), null);
    if (ObjectStore.ActiveToasts().Length > 1) throw new Exception("overlap at animation handoff");
    Time.frameCount++;
}
void Drain()
{
    for (var i = 0; i < 250; i++) Tick();
    if (ObjectStore.ActiveToasts().Length > 0) throw new Exception("queue failed to drain");
}
void Expect(params long[] expected)
{
    var actual = ObjectStore.Created.Where(g => g.name == "DeepSpacePawnshop.CreditChangeToast")
        .Select(g => g.Get<TextMeshProUGUI>()!.text)
        .Select(text => (text.StartsWith("-") ? -1 : 1) * long.Parse(text.Split(' ')[1].Replace(",", "").Replace("信用点", "")))
        .ToArray();
    if (!actual.SequenceEqual(expected)) throw new Exception($"Amounts: [{string.Join(',', actual)}] != [{string.Join(',', expected)}]");
}
void Sale(Action action)
    => Order("SellMultiple", action);
void WholesaleItem(long amount)
{
    if (!WholesaleCreditToastPatch.TryCollectItemExpense(amount)) CreditChangeToast.Show(-amount);
}
void Wholesale(int paid, params long[] costs)
{
    PlayerStore.Instance = new PlayerStore { playerCash = 1000 };
    Order("OnWholesaleClick", () =>
    {
        PlayerStore.Instance.playerCash -= paid;
        WholesaleCreditToastPatch.ObservePayment();
        foreach (var cost in costs) WholesaleItem(cost);
    });
}
void Order(string name, Action action)
{
    var patch = typeof(MultipleSaleCreditToastPatch).Assembly.GetTypes().FirstOrDefault(type =>
        type.GetCustomAttributes<HarmonyLib.HarmonyPatch>().Any(attribute => attribute.Type == typeof(NegociationUIManager) && attribute.Name == name));
    if (patch is null)
    {
        // Execute the real unscoped callback sequence when an entry point is
        // missing; the amount assertion then reproduces per-item toasts.
        action(); return;
    }
    var prefix = patch.GetMethod("Prefix", PrivateStatic)!;
    var finalizer = patch.GetMethod("Finalizer", PrivateStatic)!;
    object?[] state = { null }; prefix.Invoke(null, state);
    Exception? failure = null;
    try { action(); }
    catch (Exception ex) { failure = ex; }
    var returned = finalizer.Invoke(null, new[] { failure, state[0] });
    if (!ReferenceEquals(returned, failure)) throw new Exception("finalizer swallowed the original failure");
    if (failure != null) throw failure;
}
