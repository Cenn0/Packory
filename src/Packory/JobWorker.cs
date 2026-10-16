using Microsoft.Extensions.Hosting;

namespace Packory;

// Persistent SQLite queue, one writer process in the supported standalone profile.
public sealed class JobWorker(V2Catalog catalog,CasStore cas,ILogger<JobWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        catalog.RecoverJobs();
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(10));
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job=catalog.ClaimJob();
                if(job is not null)
                {
                    try
                    {
                        if(job.Type!="blob.reconcile")throw new InvalidOperationException("Unsupported job type");
                        var deleted=cas.DeleteAbandonedStaging(TimeSpan.FromHours(2));
                        var orphans=cas.DeleteUnreferenced(catalog.IsReferencedDigest,TimeSpan.FromDays(1));
                        catalog.Audit("system-worker","maintenance.reconcile",job.Id.ToString(),
                            "stagingRemoved="+deleted+";unreferencedRemoved="+orphans);
                        catalog.FinishJob(job.Id,null);
                    }
                    catch(Exception ex)
                    {
                        logger.LogError(ex,"Maintenance job {JobId} failed",job.Id);
                        catalog.FinishJob(job.Id,"Job failed; see server logs");
                    }
                }
                if(!await timer.WaitForNextTickAsync(stoppingToken))break;
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                logger.LogError(ex,"Background worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(10),stoppingToken);
            }
        }
    }
}
