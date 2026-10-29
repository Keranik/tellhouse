using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Household> Households => Set<Household>();
    public DbSet<Person> Persons => Set<Person>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<SupplyItem> SupplyItems => Set<SupplyItem>();
    public DbSet<TaskNote> TaskNotes => Set<TaskNote>();
    public DbSet<JournalPrompt> JournalPrompts => Set<JournalPrompt>();
    public DbSet<EventAudit> EventAudits => Set<EventAudit>();
    public DbSet<EventAction> EventActions => Set<EventAction>();
    public DbSet<AlertDelivery> AlertDeliveries => Set<AlertDelivery>();
    public DbSet<HouseholdSchedule> HouseholdSchedules => Set<HouseholdSchedule>();
    public DbSet<AlertLevel> AlertLevels => Set<AlertLevel>();
    public DbSet<GlossaryTerm> GlossaryTerms => Set<GlossaryTerm>();
    public DbSet<SupplyAdjustment> SupplyAdjustments => Set<SupplyAdjustment>();
    public DbSet<Promise> Promises => Set<Promise>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlanElement> PlanElements => Set<PlanElement>();
    public DbSet<PlanAgreement> PlanAgreements => Set<PlanAgreement>();
    public DbSet<PlanDelivery> PlanDeliveries => Set<PlanDelivery>();
    public DbSet<PlanChangeAudit> PlanChangeAudits => Set<PlanChangeAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Store enums as readable text so the shared record stays legible
        // when a person opens the database directly.
        modelBuilder.Entity<Person>()
            .Property(p => p.Role)
            .HasConversion<string>();
        modelBuilder.Entity<Event>()
            .Property(e => e.Type)
            .HasConversion<string>();
        modelBuilder.Entity<Event>()
            .Property(e => e.ConfirmationState)
            .HasConversion<string>();
        modelBuilder.Entity<Event>()
            .Property(e => e.SensitivityClass)
            .HasConversion<string>();
        modelBuilder.Entity<EventAudit>()
            .Property(a => a.FromState)
            .HasConversion<string>();
        modelBuilder.Entity<EventAction>()
            .Property(a => a.ActionType)
            .HasConversion<string>();
        modelBuilder.Entity<Promise>()
            .Property(p => p.State)
            .HasConversion<string>();
        modelBuilder.Entity<Plan>()
            .Property(p => p.State)
            .HasConversion<string>();
        modelBuilder.Entity<PlanElement>()
            .Property(e => e.Kind)
            .HasConversion<string>();
        modelBuilder.Entity<PlanElement>()
            .Property(e => e.State)
            .HasConversion<string>();
        modelBuilder.Entity<PlanAgreement>()
            .Property(a => a.Disposal)
            .HasConversion<string>();
        modelBuilder.Entity<PlanDelivery>()
            .Property(d => d.Reason)
            .HasConversion<string>();

        // An event MAY name the person it is about and the person who
        // added it; both links are optional, because events are told to
        // the house and many are household-scope (see Entities.cs).
        // Keep both links from cascading, so removing a person never erases
        // words other people said. Unattributed events are unaffected.
        modelBuilder.Entity<Event>()
            .HasOne(e => e.SubjectPerson)
            .WithMany()
            .HasForeignKey(e => e.SubjectPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Event>()
            .HasOne(e => e.AuthorPerson)
            .WithMany()
            .HasForeignKey(e => e.AuthorPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Event>()
            .HasOne(e => e.RelatedSupplyItem)
            .WithMany()
            .HasForeignKey(e => e.RelatedSupplyItemId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SupplyItem>()
            .HasOne(s => s.AssignedPerson)
            .WithMany()
            .HasForeignKey(s => s.AssignedPersonId)
            .OnDelete(DeleteBehavior.Restrict);

        // Every person link
        // below is Restrict for the same reason as the event links. The
        // record a person's words live in is never erased by removing a
        // person, an event, or an item.
        modelBuilder.Entity<Household>()
            .HasOne(h => h.DefaultRoutePerson)
            .WithMany()
            .HasForeignKey(h => h.DefaultRoutePersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Household>()
            .HasOne(h => h.BackupRoutePerson)
            .WithMany()
            .HasForeignKey(h => h.BackupRoutePersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<EventAudit>()
            .HasOne(a => a.Event)
            .WithMany()
            .HasForeignKey(a => a.EventId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<EventAudit>()
            .HasOne(a => a.ActorPerson)
            .WithMany()
            .HasForeignKey(a => a.ActorPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<EventAction>()
            .HasOne(a => a.Event)
            .WithMany()
            .HasForeignKey(a => a.EventId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<EventAction>()
            .HasOne(a => a.TargetPerson)
            .WithMany()
            .HasForeignKey(a => a.TargetPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AlertDelivery>()
            .HasOne(d => d.Event)
            .WithMany()
            .HasForeignKey(d => d.EventId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AlertDelivery>()
            .HasOne(d => d.EventAction)
            .WithMany()
            .HasForeignKey(d => d.EventActionId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AlertDelivery>()
            .HasOne(d => d.ToPerson)
            .WithMany()
            .HasForeignKey(d => d.ToPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<HouseholdSchedule>()
            .HasOne(s => s.SetByPerson)
            .WithMany()
            .HasForeignKey(s => s.SetByPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AlertLevel>()
            .HasOne(l => l.RouteToPerson)
            .WithMany()
            .HasForeignKey(l => l.RouteToPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AlertLevel>()
            .HasOne(l => l.BackupPerson)
            .WithMany()
            .HasForeignKey(l => l.BackupPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SupplyAdjustment>()
            .HasOne(a => a.SupplyItem)
            .WithMany()
            .HasForeignKey(a => a.SupplyItemId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SupplyAdjustment>()
            .HasOne(a => a.Event)
            .WithMany()
            .HasForeignKey(a => a.EventId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SupplyAdjustment>()
            .HasOne(a => a.ActorPerson)
            .WithMany()
            .HasForeignKey(a => a.ActorPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Promise>()
            .HasOne(p => p.PromisorPerson)
            .WithMany()
            .HasForeignKey(p => p.PromisorPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Promise>()
            .HasOne(p => p.PromiseePerson)
            .WithMany()
            .HasForeignKey(p => p.PromiseePersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Promise>()
            .HasOne(p => p.SourceEvent)
            .WithMany()
            .HasForeignKey(p => p.SourceEventId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Promise>()
            .HasOne(p => p.ClosedByEvent)
            .WithMany()
            .HasForeignKey(p => p.ClosedByEventId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Promise>()
            .HasOne(p => p.ClosedByPerson)
            .WithMany()
            .HasForeignKey(p => p.ClosedByPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Plan>()
            .HasOne(p => p.AnchorEvent)
            .WithMany()
            .HasForeignKey(p => p.AnchorEventId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Plan>()
            .HasOne(p => p.RunnerPerson)
            .WithMany()
            .HasForeignKey(p => p.RunnerPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PlanElement>()
            .HasOne(e => e.AssigneePerson)
            .WithMany()
            .HasForeignKey(e => e.AssigneePersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PlanAgreement>()
            .HasOne(a => a.Person)
            .WithMany()
            .HasForeignKey(a => a.PersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PlanDelivery>()
            .HasOne(d => d.PlanElement)
            .WithMany()
            .HasForeignKey(d => d.PlanElementId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PlanDelivery>()
            .HasOne(d => d.ToPerson)
            .WithMany()
            .HasForeignKey(d => d.ToPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PlanDelivery>()
            .HasOne(d => d.ActorPerson)
            .WithMany()
            .HasForeignKey(d => d.ActorPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PlanChangeAudit>()
            .HasOne(a => a.PlanElement)
            .WithMany()
            .HasForeignKey(a => a.PlanElementId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PlanChangeAudit>()
            .HasOne(a => a.ActorPerson)
            .WithMany()
            .HasForeignKey(a => a.ActorPersonId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
