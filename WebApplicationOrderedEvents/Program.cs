using JasperFx;
using JasperFx.Core;
using Marten;
using OrderEventSourcingSample;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

// Not 100% necessary, but enables some extra command line diagnostics
builder.Host.ApplyJasperFxExtensions();

#region sample_using_the_marten_persistence_integration

builder.Services.AddControllers();
// Adding Marten
builder.Services.AddMarten(opts =>
        {
            var connectionString = builder.Configuration.GetConnectionString("Marten");
            opts.Connection(connectionString);
            opts.DatabaseSchemaName = "orders";
            
            // ConcurrencyException -> NpgsqlExceptions =_="
            // EventStreamUnexpectedMaxEventIdException: duplicate key value violates unique constraint "mt_events_default_stream_id_version_is_archived_idx"
            // to
            // EventStreamUnexpectedMaxEventIdException: duplicate key value violates unique constraint "pk_mt_events_stream_and_version"
            // opts.Events.UseArchivedStreamPartitioning = true; 

        }
    )

    // Adding the Wolverine integration for Marten.
    .IntegrateWithWolverine();

#endregion

#region sample_configure_global_exception_rules

builder.Host.UseWolverine(opts =>
    {
        opts.Policies.AutoApplyTransactions();

        // Retry policies if a Marten concurrency exception is encountered
        opts.OnException<ConcurrencyException>()
            .RetryOnce()
            .Then.RetryWithCooldown(100.Milliseconds(), 250.Milliseconds())
            .Then.Discard();
    }
);

#endregion

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddWolverineHttp();
var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapPost("/items/ready", (MarkItemReady command, IMessageBus bus) => bus.InvokeAsync(command));
app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapWolverineEndpoints(_ => { });

app.MapControllers();

return await app.RunJasperFxCommands(args);