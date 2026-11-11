using System.Net;
using Volo.Abp;
using Volo.Abp.AspNetCore.ExceptionHandling;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Autofac;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;

namespace AbpWorkflow.SampleHost;

/// <summary>
/// The engine's module and ABP's: all a host needs. The sample's context holds the engine's tables beside its
/// documents; everything else is the engine's defaults, except who's in which group (<see cref="SamplePeople"/>).
/// </summary>
[DependsOn(typeof(AbpAutofacModule), typeof(AbpAspNetCoreMvcModule), typeof(AbpWorkflowModule))]
public class SampleHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The sample's context stands in for the engine's, so both write in one transaction.
        context.Services.AddAbpDbContext<SampleDbContext>(options => options.ReplaceDbContext<IWorkflowsDbContext>());
        Configure<AbpDbContextOptions>(options => options.UseNpgsql());
        // The engine's refusals say what they are: a conflict is 409, a rule's refusal 403.
        Configure<AbpExceptionHttpStatusCodeOptions>(options =>
        {
            options.Map(WorkflowErrorCodes.Conflict, HttpStatusCode.Conflict);
            options.Map(WorkflowErrorCodes.Forbidden, HttpStatusCode.Forbidden);
        });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        IApplicationBuilder app = context.GetApplicationBuilder();
        app.UseRouting();
        app.UseConfiguredEndpoints();
    }
}
