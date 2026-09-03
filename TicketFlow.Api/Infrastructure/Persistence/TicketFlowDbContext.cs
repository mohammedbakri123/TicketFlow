namespace TicketFlow.Api.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using TicketFlow.Api.Domain.Tickets;

public class TicketFlowDbContext : DbContext
{
    public TicketFlowDbContext(DbContextOptions<TicketFlowDbContext> options)
        : base(options)
    {
    }

    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.ToTable("tickets");

            entity.HasKey(t => t.Id);

            entity.Property(t => t.Subject)
                .IsRequired()
                .HasMaxLength(255);

            entity.Property(t => t.Body)
                .IsRequired();

            entity.Property(t => t.Status)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(50);

            entity.Property(t => t.Category)
                .HasConversion<string>()
                .HasMaxLength(50)
                .IsRequired(false);

            entity.Property(t => t.Priority)
                .HasConversion<string>()
                .HasMaxLength(50)
                .IsRequired(false);

            entity.Property(t => t.Summary)
                .HasMaxLength(1000)
                .IsRequired(false);

            entity.Property(t => t.Attempts)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(t => t.CreatedAt)
                .IsRequired();

            entity.Property(t => t.UpdatedAt)
                .IsRequired();
        });
    }
}
