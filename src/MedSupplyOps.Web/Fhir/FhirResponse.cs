using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace MedSupplyOps.Web.Fhir;

public static class FhirResponse
{
    public const string ContentType = "application/fhir+json; charset=utf-8";

    public static ContentResult Resource(Resource resource, int statusCode = StatusCodes.Status200OK)
        => new()
        {
            Content = Serialize(resource),
            ContentType = ContentType,
            StatusCode = statusCode,
        };

    public static OperationOutcome Outcome(OperationOutcome.IssueType code, string diagnostics)
        => new()
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = code,
                    Diagnostics = diagnostics,
                },
            ],
        };

    public static string Serialize(Resource resource)
        // 產品回應只能由官方 Firely SDK 的 R4 POCO serializer 產生；不可改成 System.Text.Json DTO。
        => new FhirJsonSerializer().SerializeToString(resource);
}
