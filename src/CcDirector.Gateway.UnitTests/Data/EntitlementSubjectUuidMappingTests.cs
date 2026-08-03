using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The go-live proof for the hosted-enrollment 503: the <c>gateway.entitlements.subject</c> column is Postgres
/// <c>uuid</c>, but the CLR property is a string, so unless EF is told to convert it Npgsql types the
/// key-lookup parameter as text and Postgres rejects <c>subject = @p</c> with 42883
/// 'operator does not exist: uuid = text'. That rejection is exactly why the entitlement read failed and
/// enrollment answered 503.
///
/// These tests build the SAME provider-agnostic <see cref="GatewayDbContext"/> model under each provider and
/// inspect the mapping EF actually resolved for the Subject key - no live database is opened (model building
/// touches no connection), so they run in the ordinary suite:
///
///  - UNDER POSTGRES the Subject key maps through a string&lt;-&gt;Guid value converter and its store type is
///    <c>uuid</c>, so the generated parameter is a UUID, not text. This is the necessary condition for the
///    live enroll-200; the sufficient condition is the live box answering 200 after deploy.
///  - UNDER SQLITE (self-host) nothing changes: no converter is in play and the column stays a plain string,
///    proving the Postgres-only conditional correctly does nothing off Postgres.
///
/// A Postgres-branch claim MUST be evaluated under the Postgres provider - the same assertion checked on the
/// default SQLite context proves the wrong branch - so each test pins its own provider explicitly.
/// </summary>
public sealed class EntitlementSubjectUuidMappingTests
{
    /// <summary>Build the model under Npgsql. A dummy connection string is enough: only the model is read, and
    /// model building opens no connection. UseNpgsql is what makes Database.IsNpgsql() true at model-build time,
    /// which is the branch this test exists to check.</summary>
    private static GatewayDbContext NewPostgresContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql("Host=localhost;Database=ccpg_model_only;Username=u;Password=p")
            .Options;
        return new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
    }

    /// <summary>Build the model under SQLite (the self-host provider), again model-only, no connection.</summary>
    private static GatewayDbContext NewSqliteContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
    }

    private static Microsoft.EntityFrameworkCore.Metadata.IProperty SubjectProperty(GatewayDbContext ctx)
    {
        var entity = ctx.Model.FindEntityType(typeof(EntitlementEntity));
        Assert.NotNull(entity);
        var property = entity!.FindProperty(nameof(EntitlementEntity.Subject));
        Assert.NotNull(property);
        return property!;
    }

    [Fact]
    public void UnderPostgres_SubjectKey_MapsAsUuid_ThroughAStringToGuidConverter()
    {
        using var ctx = NewPostgresContext();
        var subject = SubjectProperty(ctx);

        // It is the primary key - so this is the parameter the entitlement read binds. If a value converter on
        // a key property were dropped by EF the read would revert to text, so proving it here proves the key
        // lookup itself types as uuid.
        Assert.True(subject.IsPrimaryKey());

        // A value converter is applied and its provider side is Guid, so EF sends a Guid to Npgsql.
        var converter = subject.GetValueConverter();
        Assert.NotNull(converter);
        Assert.Equal(typeof(string), converter!.ModelClrType);
        Assert.Equal(typeof(Guid), converter.ProviderClrType);

        // The RESOLVED store type is uuid - this is the type that decides the parameter's NpgsqlDbType. Text
        // here would be the defect; uuid is the fix.
        var mapping = subject.FindRelationalTypeMapping();
        Assert.NotNull(mapping);
        Assert.Equal("uuid", mapping!.StoreType);

        // A round-trip through the converter is the canonical "D" string both ways, so the property stays a
        // plain string for the token validator and the entitlement registry.
        var uid = "35543491-85cb-468d-a0c9-560193683105";
        var toProvider = converter.ConvertToProvider(uid);
        Assert.IsType<Guid>(toProvider);
        Assert.Equal(Guid.Parse(uid), (Guid)toProvider!);
        Assert.Equal(uid, converter.ConvertFromProvider(toProvider));
    }

    [Fact]
    public void UnderSqlite_SubjectKey_StaysAPlainString_WithNoConverter()
    {
        using var ctx = NewSqliteContext();
        var subject = SubjectProperty(ctx);

        // Still the key, but with no Guid converter in play - the Postgres-only conditional does nothing here.
        Assert.True(subject.IsPrimaryKey());
        Assert.Null(subject.GetValueConverter());

        var mapping = subject.FindRelationalTypeMapping();
        Assert.NotNull(mapping);
        Assert.Equal(typeof(string), mapping!.ClrType);
        Assert.NotEqual("uuid", mapping.StoreType);
    }
}
