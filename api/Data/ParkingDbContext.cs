using Microsoft.EntityFrameworkCore;
using ParkingApiPg.Models;
namespace ParkingApiPg.Data;

public class ParkingDbContext : DbContext
{
    public ParkingDbContext(DbContextOptions<ParkingDbContext> options) : base(options) { }
    public DbSet<LiveParking> LiveParking => Set<LiveParking>();
    public DbSet<GateDevice> GateDevices => Set<GateDevice>();
    public DbSet<GateLog> GateLog => Set<GateLog>();
    public DbSet<GatePayment> GatePayments => Set<GatePayment>();
    public DbSet<EventCalendarEntry> EventCalendar => Set<EventCalendarEntry>();
}
