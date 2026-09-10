using System.Globalization;
using Hl7.Fhir.Model;
using MedSupplyOps.Infrastructure.Queries;

namespace MedSupplyOps.Web.Fhir;

public static class FhirResourceMapper
{
    public const string RequisitionNoSystem = "https://medsupplyops.example.org/fhir/sid/requisition-no";
    public const string RequisitionLineSystem = "https://medsupplyops.example.org/fhir/sid/requisition-line";
    public const string IssueAllocationSystem = "https://medsupplyops.example.org/fhir/sid/issue-allocation";
    public const string ItemCodeSystem = "https://medsupplyops.example.org/fhir/CodeSystem/item";

    public static SupplyRequest ToSupplyRequest(FhirSupplyRequestData row)
        => new()
        {
            Id = row.RequisitionLineId.ToString(CultureInfo.InvariantCulture),
            Identifier =
            [
                new Identifier(RequisitionNoSystem, row.RequisitionNo),
                new Identifier(RequisitionLineSystem, row.RequisitionLineId.ToString(CultureInfo.InvariantCulture)),
            ],
            // FHIR R4 的狀態比本系統粗：PendingApproval 與 Approved 刻意都成為 active。
            // 不可自造 pending-approval，否則違反 required value set；完整資訊損失見 docs/integration/fhir.md。
            Status = ToSupplyRequestStatus(row.RequisitionStatus),
            Item = ItemConcept(row.ItemCode, row.ItemName, row.Specification),
            Quantity = QuantityOf(row.Quantity, row.UnitOfMeasure),
            AuthoredOn = ToUtcDateTime(row.CreatedAt),
        };

    public static SupplyRequest.SupplyRequestStatus ToSupplyRequestStatus(string status)
        => status switch
        {
            "Draft" => SupplyRequest.SupplyRequestStatus.Draft,
            "PendingApproval" => SupplyRequest.SupplyRequestStatus.Active,
            "Approved" => SupplyRequest.SupplyRequestStatus.Active,
            "Rejected" => SupplyRequest.SupplyRequestStatus.Cancelled,
            "Issued" => SupplyRequest.SupplyRequestStatus.Completed,
            "Closed" => SupplyRequest.SupplyRequestStatus.Completed,
            _ => throw new InvalidOperationException($"無法對映未知的請領狀態：{status}"),
        };

    public static SupplyDelivery ToSupplyDelivery(FhirSupplyDeliveryData row)
    {
        var containedDeviceId = $"device-{row.IssueAllocationId.ToString(CultureInfo.InvariantCulture)}";
        var device = new Device
        {
            Id = containedDeviceId,
            Type = ItemConcept(row.ItemCode, row.ItemName, row.Specification),
            LotNumber = row.LotNumber,
            // 必須使用發料當下的快照，而不是目前 stock_lots.expiry_date；這是召回追溯語意。
            ExpirationDate = row.ExpiryDateAtIssue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        return new SupplyDelivery
        {
            Id = row.IssueAllocationId.ToString(CultureInfo.InvariantCulture),
            Identifier =
            [
                new Identifier(
                    IssueAllocationSystem,
                    row.IssueAllocationId.ToString(CultureInfo.InvariantCulture)),
            ],
            BasedOn = [new ResourceReference($"SupplyRequest/{row.RequisitionLineId.ToString(CultureInfo.InvariantCulture)}")],
            Status = SupplyDelivery.SupplyDeliveryStatus.Completed,
            SuppliedItem = new SupplyDelivery.SuppliedItemComponent
            {
                Quantity = QuantityOf(row.Quantity, row.UnitOfMeasure),
                Item = new ResourceReference($"#{containedDeviceId}"),
            },
            Occurrence = new FhirDateTime(ToUtcDateTime(row.IssuedAt)),
            Contained = [device],
        };
    }

    public static Bundle ToSearchBundle(IEnumerable<Resource> resources)
    {
        var entries = resources
            .Select(resource => new Bundle.EntryComponent
            {
                FullUrl = $"https://medsupplyops.example.org/fhir/{resource.TypeName}/{resource.Id}",
                Resource = resource,
                Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match },
            })
            .ToList();

        return new Bundle
        {
            Type = Bundle.BundleType.Searchset,
            Total = entries.Count,
            Entry = entries,
        };
    }

    public static CapabilityStatement ToCapabilityStatement()
        => new()
        {
            Url = "https://medsupplyops.example.org/fhir/metadata",
            Name = "MedSupplyOpsCapabilityStatement",
            Status = PublicationStatus.Active,
            Date = "2026-09-10",
            Kind = CapabilityStatementKind.Instance,
            Implementation = new CapabilityStatement.ImplementationComponent
            {
                Description = "MedSupplyOps FHIR R4 唯讀介接",
                Url = "https://medsupplyops.example.org/fhir",
            },
            FhirVersion = FHIRVersion.N4_0_1,
            Format = ["json"],
            Rest =
            [
                new CapabilityStatement.RestComponent
                {
                    Mode = CapabilityStatement.RestfulCapabilityMode.Server,
                    Resource =
                    [
                        new CapabilityStatement.ResourceComponent
                        {
                            Type = "SupplyRequest",
                            Interaction =
                            [
                                new CapabilityStatement.ResourceInteractionComponent
                                {
                                    Code = CapabilityStatement.TypeRestfulInteraction.Read,
                                },
                                new CapabilityStatement.ResourceInteractionComponent
                                {
                                    Code = CapabilityStatement.TypeRestfulInteraction.SearchType,
                                },
                            ],
                            SearchParam =
                            [
                                new CapabilityStatement.SearchParamComponent
                                {
                                    Name = "identifier",
                                    Type = SearchParamType.Token,
                                },
                            ],
                        },
                        new CapabilityStatement.ResourceComponent
                        {
                            Type = "SupplyDelivery",
                            Interaction =
                            [
                                new CapabilityStatement.ResourceInteractionComponent
                                {
                                    Code = CapabilityStatement.TypeRestfulInteraction.Read,
                                },
                                new CapabilityStatement.ResourceInteractionComponent
                                {
                                    Code = CapabilityStatement.TypeRestfulInteraction.SearchType,
                                },
                            ],
                            SearchParam =
                            [
                                new CapabilityStatement.SearchParamComponent
                                {
                                    Name = "based-on",
                                    Type = SearchParamType.Reference,
                                },
                            ],
                        },
                    ],
                },
            ],
        };

    private static CodeableConcept ItemConcept(string code, string name, string? specification)
        => new(ItemCodeSystem, code, name, specification is null ? name : $"{name} {specification}");

    private static Quantity QuantityOf(decimal value, string displayUnit)
        // 既有 unit_of_measure 尚未綁定 UCUM；只填顯示單位，避免把內部字串誤宣告成 UCUM code。
        => new() { Value = value, Unit = displayUnit };

    private static string ToUtcDateTime(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
}
