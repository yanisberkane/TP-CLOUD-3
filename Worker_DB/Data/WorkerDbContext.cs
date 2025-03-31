using Microsoft.EntityFrameworkCore;
using Worker_DB.Models;

namespace Worker_DB.Data;

public class WorkerDbContext : DbContext
{
    public WorkerDbContext(DbContextOptions<WorkerDbContext> options) : base(options) { }

    public DbSet<Post> Posts { get; set; }
    public DbSet<Comment> Comments { get; set; }
}
