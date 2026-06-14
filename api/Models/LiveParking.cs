using System.ComponentModel.DataAnnotations.Schema;
namespace ParkingApiPg.Models;

[Table("Live_Parking")]
public class LiveParking
{
    public int Id { get; set; }
    public string Ticket_ID { get; set; } = "";
    public string Vehicle_ID { get; set; } = "";
    public DateTime Entry_Time { get; set; }
    public DateTime? Exit_Time { get; set; }
    public decimal Parking_Fee { get; set; }
    public string Parking_Level { get; set; } = "";
    public string Vehicle_Type { get; set; } = "";
    public string Payment_Type { get; set; } = "";
    public double? Parking_Duration_Hours { get; set; }
    public string Event_Status { get; set; } = "Non-Event Day";
    public string Event_Name { get; set; } = "";
}
