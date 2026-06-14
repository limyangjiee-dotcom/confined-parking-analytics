namespace ParkingApiPg.Models;
public record EntryDto(string VehicleId, string ParkingLevel, string VehicleType, string PaymentType,
                       string EventStatus = "Non-Event Day", string EventName = "");
public record ExitDto(string TicketId, decimal ParkingFee);
public record ForecastDto(DateTime ForecastDate, double PredictedOccupancy, decimal PredictedRevenue);
