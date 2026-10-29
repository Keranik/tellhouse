using Microsoft.EntityFrameworkCore;

namespace HomeNode.Data;

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
    public DbSet<HouseConnection> HouseConnections => Set<HouseConnection>();
    public DbSet<DoorInvite> DoorInvites => Set<DoorInvite>();
    public DbSet<JoinRequest> JoinRequests => Set<JoinRequest>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();
    public DbSet<PacketInbox> PacketInbox => Set<PacketInbox>();
    public DbSet<PacketTrailLine> PacketTrailLines => Set<PacketTrailLine>();
    public DbSet<SessionChallenge> SessionChallenges => Set<SessionChallenge>();
    public DbSet<SealedOutboxItem> SealedOutbox => Set<SealedOutboxItem>();
    public DbSet<PacketSeen> PacketSeen => Set<PacketSeen>();
    public DbSet<PacketNonceUse> PacketNonceUses => Set<PacketNonceUse>();
    public DbSet<PersonAudienceDefault> PersonAudienceDefaults => Set<PersonAudienceDefault>();
    public DbSet<AudienceDefaultRequest> AudienceDefaultRequests => Set<AudienceDefaultRequest>();
    public DbSet<AudienceAsk> AudienceAsks => Set<AudienceAsk>();

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
        modelBuilder.Entity<HouseConnection>()
            .Property(c => c.Standing)
            .HasConversion<string>();
        modelBuilder.Entity<PacketInbox>()
            .HasIndex(p => p.PacketId)
            .IsUnique();
        modelBuilder.Entity<SealedOutboxItem>()
            .HasIndex(p => p.PacketId)
            .IsUnique();
        modelBuilder.Entity<PacketSeen>()
            .HasIndex(p => p.PacketId)
            .IsUnique();
        modelBuilder.Entity<PacketNonceUse>()
            .HasIndex(n => new { n.ConnectionId, n.PacketNonce })
            .IsUnique();

        // Node tables: person links are Restrict, for the same
        // reason as the record links above. Removing a person never
        // erases the door, valve, or packet trail behind them.
        modelBuilder.Entity<HouseConnection>()
            .HasOne(c => c.Person)
            .WithMany()
            .HasForeignKey(c => c.PersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<HouseConnection>()
            .HasOne(c => c.EnrollingInviterPerson)
            .WithMany()
            .HasForeignKey(c => c.EnrollingInviterPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<DoorInvite>()
            .HasOne(i => i.InviterPerson)
            .WithMany()
            .HasForeignKey(i => i.InviterPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<DoorInvite>()
            .HasOne(i => i.IntendedPerson)
            .WithMany()
            .HasForeignKey(i => i.IntendedPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<JoinRequest>()
            .HasOne(r => r.DoorInvite)
            .WithMany()
            .HasForeignKey(r => r.DoorInviteId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ConsentRecord>()
            .HasOne(c => c.Person)
            .WithMany()
            .HasForeignKey(c => c.PersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SealedOutboxItem>()
            .HasOne(o => o.ToPerson)
            .WithMany()
            .HasForeignKey(o => o.ToPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PersonAudienceDefault>()
            .Property(d => d.Value)
            .HasConversion<string>();
        modelBuilder.Entity<PersonAudienceDefault>()
            .HasIndex(d => new { d.HouseholdId, d.HolderPersonId })
            .IsUnique();
        modelBuilder.Entity<AudienceDefaultRequest>()
            .Property(r => r.Value)
            .HasConversion<string>();
        modelBuilder.Entity<AudienceDefaultRequest>()
            .Property(r => r.State)
            .HasConversion<string>();
        modelBuilder.Entity<AudienceAsk>()
            .Property(a => a.State)
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
        modelBuilder.Entity<PersonAudienceDefault>()
            .HasOne(d => d.HolderPerson)
            .WithMany()
            .HasForeignKey(d => d.HolderPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AudienceDefaultRequest>()
            .HasOne(r => r.HolderPerson)
            .WithMany()
            .HasForeignKey(r => r.HolderPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AudienceDefaultRequest>()
            .HasOne(r => r.RequestedByPerson)
            .WithMany()
            .HasForeignKey(r => r.RequestedByPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AudienceDefaultRequest>()
            .HasOne(r => r.DisposedByPerson)
            .WithMany()
            .HasForeignKey(r => r.DisposedByPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AudienceAsk>()
            .HasOne(a => a.Event)
            .WithMany()
            .HasForeignKey(a => a.EventId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AudienceAsk>()
            .HasOne(a => a.AskedPerson)
            .WithMany()
            .HasForeignKey(a => a.AskedPersonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AudienceAsk>()
            .HasOne(a => a.AnsweredByPerson)
            .WithMany()
            .HasForeignKey(a => a.AnsweredByPersonId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
