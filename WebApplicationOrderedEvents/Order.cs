using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Mvc;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Http;
using Wolverine.Marten;
using Wolverine.Persistence;

namespace OrderEventSourcingSample;

public record OrderShipped;

public record OrderCreated(Item[] Items);

public record OrderReady;

public record ShipOrder(Guid OrderId);

public record ItemReady(string Name);

#region sample_Order_event_sourced_aggregate

public class Item
{
    public string Name { get; set; }

    public bool Ready { get; set; }
}

public class Order
{
    public static Order Create(OrderCreated @event) => new(@event);

    public Order(OrderCreated created)
    {
        foreach (var item in created.Items)
            Items[item.Name] = item;
    }

    // This would be the stream id
    public Guid Id { get; set; }

    // This is important, by Marten convention this would
    // be the
    public int Version { get; set; }

    public DateTimeOffset? Shipped { get; private set; }

    public Dictionary<string, Item> Items { get; set; } = new();

    // These methods are used by Marten to update the aggregate
    // from the raw events
    public void Apply(IEvent<OrderShipped> shipped)
    {
        Shipped = shipped.Timestamp;
    }

    public void Apply(ItemReady ready)
    {
        Items[ready.Name].Ready = true;
    }

    public bool IsReadyToShip()
    {
        return Shipped == null && Items.Values.All(x => x.Ready);
    }
}

#endregion

public record MarkItemReady(Guid Id, string ItemName);

#region AggregateController_MarkItemReady

public static class MarkItemReadyHandler
{
    [AggregateHandler]
    [WolverinePost("/api/v1/mark-ready/")]
    public static IEnumerable<object> Handle(MarkItemReady command, Order order)
    {
        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
            item.Ready = true;

            yield return new ItemReady(command.ItemName);
        }
        else
        {
            throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
        }

        if (order.IsReadyToShip())
        {
            yield return new OrderReady();
        }
    }
}

#endregion

#region ControllerBase_MarkItemReady

public class MarkItemController : ControllerBase
{
    [HttpPost("/api/v1/mark-ready-controller/")]
    public async Task Post(
        [FromBody] MarkItemReady command,
        [FromServices] IDocumentSession session,
        [FromServices] IMartenOutbox outbox)
    {
        // This is important!
        outbox.Enroll(session);

        // Fetch the current value of the Order aggregate
        var stream = await session
            .Events

            // We're also opting into Marten optimistic concurrency checks here
            .FetchForWriting<Order>(command.Id);

        var order = stream.Aggregate;

        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
            item.Ready = true;

            // Mark that the this item is ready
            stream.AppendOne(new ItemReady(command.ItemName));
        }
        else
        {
            // Some crude validation
            throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
        }

        // If the order is ready to ship, also emit an OrderReady event
        if (order.IsReadyToShip())
        {
            // Publish a cascading command to do whatever it takes
            // to actually ship the order
            // Note that because the context here is enrolled in a Wolverine
            // outbox, the message is registered, but not "released" to
            // be sent out until SaveChangesAsync() is called down below
            await outbox.PublishAsync(new ShipOrder(command.Id));
            stream.AppendOne(new OrderReady());
        }

        // This will also persist and flush out any outgoing messages
        // registered into the context outbox
        await session.SaveChangesAsync();
    }
}

#endregion

#region Create

public static class Create
{
    public record CreateRequest(Guid Id, string ItemName);

    [WolverinePost("/api/v1/create-stream/")]
    public static (CreationResponse<Guid>, IStartStream) Handle(CreateRequest request)
    {
        var start = MartenOps.StartStream<Order>(
            request.Id,
            new OrderCreated(
                [
                    new Item
                    {
                        Name =  request.ItemName,
                        Ready = true
                    }
                ]
            )
        );

        var response = new CreationResponse<Guid>("/api/order/" + start.StreamId, start.StreamId);

        return (response, start);
    }
}

#endregion