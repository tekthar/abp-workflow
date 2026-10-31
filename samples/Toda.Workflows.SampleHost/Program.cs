using Toda.Workflows.SampleHost;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

// A host of toda-workflow alone (ADR 0089, S7): documents in its own table move by workflows on the engine, with the
// engine's defaults for everything a host may give (extensions from DI, no plugins, no site checks, no events).
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseAutofac();
await builder.AddApplicationAsync<SampleHostModule>();

WebApplication app = builder.Build();
await app.InitializeApplicationAsync();
await CreateDatabaseAsync(app.Services);
await app.RunAsync();

// The sample makes its tables from the model. A real host adds the engine's tables to its own migrations
// (dotnet ef migrations add), as it does its own, and runs them on start or in its deploy.
static async Task CreateDatabaseAsync(IServiceProvider services)
{
    using IServiceScope scope = services.CreateScope();
    using IUnitOfWork uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin();
    SampleDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextProvider<SampleDbContext>>().GetDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await uow.CompleteAsync();
}

/// <summary>The program, for the tests' WebApplicationFactory.</summary>
public partial class Program;
