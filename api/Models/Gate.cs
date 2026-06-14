using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace ParkingApiPg.Models;

// Per-device API keys: each barrier gate / camera authenticates with its own
// key (X-Device-Key header) so a leaked key can be revoked per device.
[Table("Gate_Devices")]
public class GateDevice
{
    [Key] public string Device_ID { get; set; } = "";
    public string Api_Key { get; set; } = "";
    public string Gate_Type { get; set; } = "entry";   // entry / exit
    public string Location { get; set; } = "";
    public bool Is_Active { get; set; } = true;
}

// Audit trail: every gate decision (open or deny) is logged.
[Table("Gate_Log")]
public class GateLog
{
    public int Id { get; set; }
    public DateTime Log_Time { get; set; }
    public string Device_ID { get; set; } = "";
    public string Plate { get; set; } = "";
    public string Action { get; set; } = "";           // open / deny
    public string Reason { get; set; } = "";
    public string Ticket_ID { get; set; } = "";
    public double? Confidence { get; set; }
}

// Payments made at autopay stations; exit gate checks these.
[Table("Gate_Payments")]
public class GatePayment
{
    public int Id { get; set; }
    public string Ticket_ID { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime Paid_At { get; set; }
    public string Method { get; set; } = "Autopay";
}

// Existing table (created by 01_create_event_calendar.sql) — read-only here,
// used to stamp gate entries with today's event.
[Table("Event_Calendar")]
public class EventCalendarEntry
{
    [Key] public DateTime Event_Date { get; set; }
    public string Event_Name { get; set; } = "";
    public string Expected_Scale { get; set; } = "";
}

public record GateEntryDto(string Plate, double? Confidence = null,
                           string VehicleType = "Car", string ParkingLevel = "L1");
public record GateExitDto(string? Plate = null, string? TicketId = null, double? Confidence = null);
public record GatePayDto(string? TicketId = null, string? Plate = null, string Method = "Autopay");
