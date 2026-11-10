using PurchaseOrders.Api.Services;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.Api.Endpoints;

public static class PurchaseOrderEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/suppliers", (PurchaseOrderService service, CancellationToken ct) => service.GetActiveSuppliersAsync(ct))
            .WithName("ListSuppliers");

        var orders = api.MapGroup("/purchase-orders").WithTags("PurchaseOrders");

        orders.MapGet("/", (PurchaseOrderService service, string? supplierCode, int? take, CancellationToken ct) =>
                service.ListAsync(supplierCode, take ?? 50, ct))
            .WithName("ListPurchaseOrders");

        orders.MapGet("/duplicates", (PurchaseOrderService service, CancellationToken ct) =>
                service.ListDuplicatesAsync(ct))
            .WithName("ListDuplicatePurchaseOrders")
            .WithSummary("List all duplicate purchase orders")
            .WithDescription("Matches supplier, status, notes, totals, and every line item, excluding PO identity and creation time.")
            .Produces<IReadOnlyList<PurchaseOrderDto>>();

        orders.MapGet("/duplicates/groups", (PurchaseOrderService service, CancellationToken ct) =>
                service.ListDuplicateGroupsAsync(ct))
            .WithName("ListDuplicatePurchaseOrderGroups")
            .WithSummary("List duplicate groups using the existing content matching rules")
            .Produces<IReadOnlyList<IReadOnlyList<PurchaseOrderDto>>>();

        orders.MapGet("/{id:guid}", async (Guid id, PurchaseOrderService service, CancellationToken ct) =>
                await service.GetAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound())
            .WithName("GetPurchaseOrder")
            .Produces<PurchaseOrderDto>()
            .Produces(StatusCodes.Status404NotFound);

        orders.MapPost("/", async (CreatePurchaseOrderRequest request, HttpContext context, PurchaseOrderService service, CancellationToken ct) =>
            {
                var keys = context.Request.Headers["Idempotency-Key"];
                if (keys.Count != 1 || !Guid.TryParse(keys[0], out var submissionId) || submissionId == Guid.Empty)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["Idempotency-Key"] = ["Supply exactly one nonempty UUID, reused for retries of this submission."],
                    });
                }

                return await service.CreateDraftAsync(submissionId, request, ct) switch
                {
                    CreateDraftResult.Created created => Results.Created($"/api/purchase-orders/{created.Order.Id}", created.Order),
                    CreateDraftResult.Invalid invalid => Results.ValidationProblem(invalid.Errors),
                    CreateDraftResult.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "Submission identity already used with different purchase-order data."),
                    _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
                };
            })
            .WithName("CreateDraftPurchaseOrder")
            .Produces<PurchaseOrderDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem();

        return app;
    }
}
