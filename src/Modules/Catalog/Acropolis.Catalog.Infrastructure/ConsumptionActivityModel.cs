using Microsoft.EntityFrameworkCore;

namespace Acropolis.Catalog.Infrastructure;

public static class ConsumptionActivityModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<ConsumptionSession>(entity =>
        {
            entity.ToTable("ConsumptionSessions", table =>
            {
                table.HasCheckConstraint("CK_ConsumptionSessions_Binding", "\"AuthenticationBindingHash\" ~ '^[a-f0-9]{64}$'");
                table.HasCheckConstraint("CK_ConsumptionSessions_Version", "\"ContentVersion\" ~ '^[a-f0-9]{32}$'");
                table.HasCheckConstraint("CK_ConsumptionSessions_Kind", "\"SourceKind\" IN ('reading','youtube')");
                table.HasCheckConstraint("CK_ConsumptionSessions_Category", "\"CategoryAtStart\" IN ('lecturas','documentales','videos','podcast','charlas-online')");
                table.HasCheckConstraint("CK_ConsumptionSessions_Sequence", "\"LastSequence\" BETWEEN 0 AND 1000000");
                table.HasCheckConstraint("CK_ConsumptionSessions_Coverage", "jsonb_typeof(\"CoverageJson\")='array' AND jsonb_array_length(\"CoverageJson\")<=512");
                table.HasCheckConstraint("CK_ConsumptionSessions_Duration", "\"DurationMs\" IS NULL OR \"DurationMs\" BETWEEN 1 AND 86400000");
                table.HasCheckConstraint("CK_ConsumptionSessions_Time", "\"LastReceivedUtc\">=\"StartedUtc\"");
            });
            entity.Property(x => x.AuthenticationBindingHash).HasMaxLength(64);
            entity.Property(x => x.ContentVersion).HasMaxLength(32);
            entity.Property(x => x.CategoryAtStart).HasMaxLength(32);
            entity.Property(x => x.SourceKind).HasMaxLength(16);
            entity.Property(x => x.CoverageJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.AccountId, x.VisitId }).IsUnique();
            entity.HasIndex(x => new { x.AccountId, x.StartedUtc, x.Id });
            entity.HasIndex(x => new { x.StartedUtc, x.Id });
            entity.HasIndex(x => new { x.ContentId, x.StartedUtc, x.Id });
            entity.HasOne<EditorialContent>().WithMany().HasForeignKey(x => x.ContentId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ConsumptionPulse>(entity =>
        {
            entity.HasKey(x => new { x.SessionId, x.Sequence });
            entity.Property(x => x.CanonicalHash).HasMaxLength(64);
            entity.ToTable("ConsumptionPulses", table =>
            {
                table.HasCheckConstraint("CK_ConsumptionPulses_Sequence", "\"Sequence\" BETWEEN 1 AND 1000000");
                table.HasCheckConstraint("CK_ConsumptionPulses_Hash", "\"CanonicalHash\" ~ '^[a-f0-9]{64}$'");
                table.HasCheckConstraint("CK_ConsumptionPulses_Credit", "\"CreditedMs\" BETWEEN 0 AND 15000");
                table.HasCheckConstraint("CK_ConsumptionPulses_Progress", "\"ProgressBasisPoints\" IS NULL OR \"ProgressBasisPoints\" BETWEEN 0 AND 10000");
                table.HasCheckConstraint("CK_ConsumptionPulses_End", "NOT \"EndedNow\" OR \"EndedReported\"");
            });
            entity.HasIndex(x => new { x.ReceivedUtc, x.SessionId, x.Sequence });
            entity.HasOne<ConsumptionSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ConsumptionDaily>(entity =>
        {
            entity.HasKey(x => new { x.DayUtc, x.ContentId, x.ContentVersion, x.CategoryAtStart, x.SourceKind });
            entity.Property(x => x.ContentVersion).HasMaxLength(32);
            entity.Property(x => x.CategoryAtStart).HasMaxLength(32);
            entity.Property(x => x.SourceKind).HasMaxLength(16);
            entity.ToTable("ConsumptionDaily", table =>
            {
                table.HasCheckConstraint("CK_ConsumptionDaily_Version", "\"ContentVersion\" ~ '^[a-f0-9]{32}$'");
                table.HasCheckConstraint("CK_ConsumptionDaily_Kind", "\"SourceKind\" IN ('reading','youtube')");
                table.HasCheckConstraint("CK_ConsumptionDaily_Category", "\"CategoryAtStart\" IN ('lecturas','documentales','videos','podcast','charlas-online')");
                table.HasCheckConstraint("CK_ConsumptionDaily_Counts", "\"Starts\">=0 AND \"RecordedPulses\">=0 AND \"CreditedMs\">=0 AND \"EndedReports\">=0 AND \"KnownProgressSamples\">=0 AND \"UnknownProgressSamples\">=0 AND \"ProgressBasisPointsSum\">=0 AND \"ProgressBasisPointsSum\"<=\"KnownProgressSamples\"::numeric*10000 AND \"KnownProgressSamples\"+\"UnknownProgressSamples\"=\"RecordedPulses\" AND \"EndedReports\"<=\"RecordedPulses\"");
            });
            entity.HasIndex(x => x.ContentId);
            entity.HasOne<EditorialContent>().WithMany().HasForeignKey(x => x.ContentId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ConsumptionAccountDaily>(entity =>
        {
            entity.HasKey(x => new { x.AccountId, x.DayUtc, x.ContentId, x.ContentVersion, x.CategoryAtStart, x.SourceKind });
            entity.Property(x => x.ContentVersion).HasMaxLength(32);
            entity.Property(x => x.CategoryAtStart).HasMaxLength(32);
            entity.Property(x => x.SourceKind).HasMaxLength(16);
            entity.ToTable("ConsumptionAccountDaily", table =>
            {
                table.HasCheckConstraint("CK_ConsumptionAccountDaily_Version", "\"ContentVersion\" ~ '^[a-f0-9]{32}$'");
                table.HasCheckConstraint("CK_ConsumptionAccountDaily_Kind", "\"SourceKind\" IN ('reading','youtube')");
                table.HasCheckConstraint("CK_ConsumptionAccountDaily_Category", "\"CategoryAtStart\" IN ('lecturas','documentales','videos','podcast','charlas-online')");
                table.HasCheckConstraint("CK_ConsumptionAccountDaily_Counts", "\"Starts\">=0 AND \"RecordedPulses\">=0 AND \"CreditedMs\">=0 AND \"EndedReports\">=0 AND \"KnownProgressSamples\">=0 AND \"UnknownProgressSamples\">=0 AND \"ProgressBasisPointsSum\">=0 AND \"ProgressBasisPointsSum\"<=\"KnownProgressSamples\"::numeric*10000 AND \"KnownProgressSamples\"+\"UnknownProgressSamples\"=\"RecordedPulses\" AND \"EndedReports\"<=\"RecordedPulses\"");
            });
            entity.HasIndex(x => x.ContentId);
            entity.HasIndex(x => new { x.DayUtc, x.AccountId });
            entity.HasOne<EditorialContent>().WithMany().HasForeignKey(x => x.ContentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
