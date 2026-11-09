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
            .WithSummary("List duplicate purchase orders grouped by matching content")
            .WithDescription("Uses the same matching rules as the flat duplicates endpoint.")
            .Produces<IReadOnlyList<DuplicatePurchaseOrderGroupDto>>();

        orders.MapGet("/{id:guid}", async (Guid id, PurchaseOrderService service, CancellationToken ct) =>
                await service.GetAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound())
            .WithName("GetPurchaseOrder")
            .Produces<PurchaseOrderDto>()
            .Produces(StatusCodes.Status404NotFound);

        orders.MapPost("/", async (CreatePurchaseOrderRequest request, HttpRequest httpRequest, PurchaseOrderService service, CancellationToken ct) =>
        {
            var keys = httpRequest.Headers["Idempotency-Key"];
            if (keys.Count != 1 || !Guid.TryParse(keys[0], out var submissionId) || submissionId == Guid.Empty)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["Idempotency-Key"] = ["Supply one non-empty UUID, reused for retries of this submission."],
                });
            }

            return await service.CreateDraftAsync(request, submissionId, ct) switch
                {
                    CreateDraftResult.Created created => Results.Created($"/api/purchase-orders/{created.Order.Id}", created.Order),
                    CreateDraftResult.Invalid invalid => Results.ValidationProblem(invalid.Errors),
                    CreateDraftResult.Conflict => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Submission identity already used with a different payload."),
                    _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
                };
        })
            .WithName("CreateDraftPurchaseOrder")
            .Produces<PurchaseOrderDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithDescription("Requires an Idempotency-Key UUID. Reuse it for retries; use a new UUID for a new submission.");

        return app;
    }
}
