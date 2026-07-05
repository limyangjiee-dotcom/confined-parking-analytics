namespace ParkingApiPg.Models;

// Field mapping: the names of the JSON properties in the external system's
// API response that correspond to each of our canonical session fields.
public record Mapping(string Plate = "", string EntryTime = "",
                      string ExitTime = "", string Fee = "", string Level = "", string VehicleType = "",
                      string Payment = "");

// REST-API source: the parking system exposes an endpoint returning JSON sessions.
public record ApiSource(string Url = "", string Method = "GET", string AuthHeader = "",
                        string AuthValue = "", string RecordsPath = "");

// The saved data-source configuration: which API to call + how to map its fields.
public record DsConfig(Mapping Mapping, ApiSource? Api = null);

public record SyncResult(bool Ok, int Read, int Matched, int Inserted, string Message);

// one normalized parking session pulled from the external system
public record SessionRow(string Plate, DateTime Entry, DateTime? Exit, decimal Fee, string Level, string VehicleType,
                         string Payment = "");
