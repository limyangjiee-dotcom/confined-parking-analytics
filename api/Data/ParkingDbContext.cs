using Microsoft.EntityFrameworkCore;
using ParkingApiPg.Models;
namespace ParkingApiPg.Data;

public class ParkingDbContext : DbContext
{
    public ParkingDbContext(DbContextOptions<ParkingDbContext> options) : base(options) { }
    public DbSet<LiveParking> LiveParking => Set<LiveParking>();
}
