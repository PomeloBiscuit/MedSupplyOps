using System.Globalization;
using Hl7.Fhir.Model;
using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Fhir;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedSupplyOps.Web.Controllers;

[ApiController]
[Route("fhir")]
public sealed class FhirController : ControllerBase
{
    private readonly FhirQueries _queries;

    public FhirController(FhirQueries queries)
    {
        _queries = queries;
    }

    [HttpGet("metadata")]
    [AllowAnonymous]
    public ContentResult Metadata()
        => FhirResponse.Resource(FhirResourceMapper.ToCapabilityStatement());

    [HttpGet("SupplyRequest/{id}")]
    [Authorize(Policy = AuthorizationPolicies.FhirRead)]
    public async Task<ContentResult> ReadSupplyRequest(string id, CancellationToken cancellationToken)
    {
        if (!TryPositiveId(id, out var requisitionLineId))
        {
            return BadRequestOutcome($"SupplyRequest id 必須是正整數：{id}");
        }

        var row = await _queries.GetSupplyRequestAsync(requisitionLineId, cancellationToken);
        return row is null
            ? NotFoundOutcome($"找不到 SupplyRequest/{id}")
            : FhirResponse.Resource(FhirResourceMapper.ToSupplyRequest(row));
    }

    [HttpGet("SupplyRequest")]
    [Authorize(Policy = AuthorizationPolicies.FhirRead)]
    public async Task<ContentResult> SearchSupplyRequests(
        [FromQuery] string? identifier,
        CancellationToken cancellationToken)
    {
        if (!TryToken(identifier, out var system, out var value) ||
            !string.Equals(system, FhirResourceMapper.RequisitionNoSystem, StringComparison.Ordinal))
        {
            return BadRequestOutcome(
                $"identifier 必須是 {FhirResourceMapper.RequisitionNoSystem}|{{requisitionNo}}");
        }

        var rows = await _queries.SearchSupplyRequestsByRequisitionNoAsync(value, cancellationToken);
        var bundle = FhirResourceMapper.ToSearchBundle(rows.Select(FhirResourceMapper.ToSupplyRequest));
        return FhirResponse.Resource(bundle);
    }

    [HttpGet("SupplyDelivery/{id}")]
    [Authorize(Policy = AuthorizationPolicies.FhirRead)]
    public async Task<ContentResult> ReadSupplyDelivery(string id, CancellationToken cancellationToken)
    {
        if (!TryPositiveId(id, out var issueAllocationId))
        {
            return BadRequestOutcome($"SupplyDelivery id 必須是正整數：{id}");
        }

        var row = await _queries.GetSupplyDeliveryAsync(issueAllocationId, cancellationToken);
        return row is null
            ? NotFoundOutcome($"找不到 SupplyDelivery/{id}")
            : FhirResponse.Resource(FhirResourceMapper.ToSupplyDelivery(row));
    }

    [HttpGet("SupplyDelivery")]
    [Authorize(Policy = AuthorizationPolicies.FhirRead)]
    public async Task<ContentResult> SearchSupplyDeliveries(
        [FromQuery(Name = "based-on")] string? basedOn,
        CancellationToken cancellationToken)
    {
        const string prefix = "SupplyRequest/";
        if (basedOn is null ||
            !basedOn.StartsWith(prefix, StringComparison.Ordinal) ||
            !TryPositiveId(basedOn[prefix.Length..], out var requisitionLineId))
        {
            return BadRequestOutcome("based-on 必須是 SupplyRequest/{id}");
        }

        var rows = await _queries.SearchSupplyDeliveriesBySupplyRequestAsync(requisitionLineId, cancellationToken);
        var bundle = FhirResourceMapper.ToSearchBundle(rows.Select(FhirResourceMapper.ToSupplyDelivery));
        return FhirResponse.Resource(bundle);
    }

    [AcceptVerbs("POST", "PUT", "PATCH", "DELETE")]
    [Route("SupplyRequest")]
    [Route("SupplyRequest/{id}")]
    [Authorize(Policy = AuthorizationPolicies.FhirRead)]
    public ContentResult RejectSupplyRequestWrite()
        => MethodNotAllowedOutcome("SupplyRequest 只支援 read 與 search-type");

    [AcceptVerbs("POST", "PUT", "PATCH", "DELETE")]
    [Route("SupplyDelivery")]
    [Route("SupplyDelivery/{id}")]
    [Authorize(Policy = AuthorizationPolicies.FhirRead)]
    public ContentResult RejectSupplyDeliveryWrite()
        => MethodNotAllowedOutcome("SupplyDelivery 只支援 read 與 search-type");

    private ContentResult MethodNotAllowedOutcome(string diagnostics)
    {
        Response.Headers.Allow = "GET";
        return FhirResponse.Resource(
            FhirResponse.Outcome(OperationOutcome.IssueType.NotSupported, diagnostics),
            StatusCodes.Status405MethodNotAllowed);
    }

    private static ContentResult BadRequestOutcome(string diagnostics)
        => FhirResponse.Resource(
            FhirResponse.Outcome(OperationOutcome.IssueType.Invalid, diagnostics),
            StatusCodes.Status400BadRequest);

    private static ContentResult NotFoundOutcome(string diagnostics)
        => FhirResponse.Resource(
            FhirResponse.Outcome(OperationOutcome.IssueType.NotFound, diagnostics),
            StatusCodes.Status404NotFound);

    private static bool TryPositiveId(string value, out long id)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    private static bool TryToken(string? token, out string system, out string value)
    {
        system = string.Empty;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var separator = token.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || separator == token.Length - 1 || token.IndexOf('|', separator + 1) >= 0)
        {
            return false;
        }

        system = token[..separator];
        value = token[(separator + 1)..];
        return true;
    }
}
