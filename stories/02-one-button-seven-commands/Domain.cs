using SleipnirCommon.Models;
using SleipnirCommon.Results;
using SleipnirCore.Attributes;

namespace SleipnirStories.Story02;

// === Story 02 Domain — "One Button, Seven Commands" ===============================
// Ein UI-Klick („Bestellung aufgeben") fächert auf sieben Downstream-Commands aus.
// Separater Host → Controller-Namen brauchen kein Präfix (Story 01 läuft nicht
// nebenan); sie heißen schlicht Order, Inventory, Billing, … — app-weit eindeutig.

public sealed class CommandAck
{
    public string Service { get; set; } = "";
    public string Ref { get; set; } = "";
    public int OrderId { get; set; }
}

internal static class StoryLatency
{
    public const int MsPerCall = 30;
    public static Task Wait() => Task.Delay(MsPerCall);
}

internal static class Story02Store
{
    // Customer 7 ist für die Demo absichtlich über ihrem Kreditlimit — Billing schlägt
    // als einziger der sieben Commands fehl (Business-Regel, kein Crash).
    public static readonly HashSet<int> OverCreditLimit = new() { 7 };

    public static int NextOrderId = 1042;
    public static readonly List<string> AuditLog = new();
    public static readonly List<int> ReservedArticles = new();
    public static int AwardedPoints;
    public static bool NotificationSent;
    public static int ScheduledShipment;
}

// === Die sieben Commands ==========================================================

[SleipnirController("Order")]
public class OrderController
{
    // Provider: legt die Bestellung an, exposet die neue OrderId für die drei
    // Downstream-Commands, die die OrderId brauchen (Notification, Audit, Shipping).
    [SleipnirMethod("Create")]
    public async Task<CommandAck> Create(int customerId, int addressId, List<int> articleIds)
    {
        await StoryLatency.Wait();
        var orderId = Interlocked.Increment(ref Story02Store.NextOrderId);
        return new CommandAck { Service = "Order", Ref = $"ord-{orderId} (customerId={customerId}, {articleIds.Count} lines)", OrderId = orderId };
    }
}

[SleipnirController("Inventory")]
public class InventoryController
{
    [SleipnirMethod("Reserve")]
    public async Task<CommandAck> Reserve(List<int> articleIds)
    {
        await StoryLatency.Wait();
        Story02Store.ReservedArticles.AddRange(articleIds);
        return new CommandAck { Service = "Inventory", Ref = $"reserved {articleIds.Count} articles" };
    }
}

[SleipnirController("Billing")]
public class BillingController
{
    // Der einzige Command, der bewusst einen Business-Fehler zurückgibt (für Customer
    // im Over-Credit-Limit-Set). Gibt SleipnirResults.Error zurück — NIEMALS werfen, um
    // einen client-sichtbaren Code+Message zu setzen (SleipnirResults.Error → ReturnResponse).
    [SleipnirMethod("Charge")]
    public async Task<SleipnirResponse> Charge(int customerId, decimal amount)
    {
        await StoryLatency.Wait();
        if (Story02Store.OverCreditLimit.Contains(customerId))
            return SleipnirResults.Error(402, $"Credit limit exceeded for customer {customerId} (amount {amount:F2}).");
        return SleipnirResults.Ok(new CommandAck { Service = "Billing", Ref = $"charged {amount:F2}" });
    }
}

[SleipnirController("Loyalty")]
public class LoyaltyController
{
    [SleipnirMethod("AwardPoints")]
    public async Task<CommandAck> AwardPoints(int customerId, decimal amount)
    {
        await StoryLatency.Wait();
        var points = (int)(amount * 10);
        Story02Store.AwardedPoints += points;
        return new CommandAck { Service = "Loyalty", Ref = $"awarded {points} points (customerId={customerId})" };
    }
}

[SleipnirController("Notification")]
public class NotificationController
{
    // Consumer von @orderId (Parameter heißt `orderId` → Bindung nach Name).
    [SleipnirMethod("SendConfirmation")]
    public async Task<CommandAck> SendConfirmation(int customerId, int orderId)
    {
        await StoryLatency.Wait();
        Story02Store.NotificationSent = true;
        return new CommandAck { Service = "Notification", Ref = $"confirmation sent (orderId={orderId}, customerId={customerId})" };
    }
}

[SleipnirController("Audit")]
public class AuditController
{
    [SleipnirMethod("Log")]
    public async Task<CommandAck> Log(int orderId, string action)
    {
        await StoryLatency.Wait();
        var entry = $"{action} @ order {orderId}";
        Story02Store.AuditLog.Add(entry);
        return new CommandAck { Service = "Audit", Ref = entry };
    }
}

[SleipnirController("Shipping")]
public class ShippingController
{
    [SleipnirMethod("Schedule")]
    public async Task<CommandAck> Schedule(int orderId, int addressId)
    {
        await StoryLatency.Wait();
        Story02Store.ScheduledShipment = orderId;
        return new CommandAck { Service = "Shipping", Ref = $"shipment scheduled (orderId={orderId}, addressId={addressId})" };
    }
}