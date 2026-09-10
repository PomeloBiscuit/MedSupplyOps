using System.Globalization;
using System.Net;
using Dapper;
using Firely.Fhir.Validation;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;
using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Infrastructure.Services;
using MedSupplyOps.Web.Fhir;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed class FhirApiTests : IClassFixture<ApplicationStartupSmokeTests.ProductionLikeFactory>
{
    private static readonly DateOnly Today = TestBusinessCalendar.SystemToday;
    private readonly ApplicationStartupSmokeTests.ProductionLikeFactory _factory;
    private readonly ITestOutputHelper _output;

    public FhirApiTests(ApplicationStartupSmokeTests.ProductionLikeFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task Identifier_search_returns_one_valid_SupplyRequest_per_line()
    {
        await using var scenario = await FhirTestScenario.CreateAsync(itemCount: 2);
        var requisition = await scenario.CreateRequisitionAsync("Approved", (0, 3), (1, 7));
        using var client = await CreateAuthenticatedClientAsync(TestIdentitySeeder.StorekeeperEmail);

        var uri = "/fhir/SupplyRequest?identifier=" + Uri.EscapeDataString(
            $"{FhirResourceMapper.RequisitionNoSystem}|{requisition.RequisitionNo}");
        var response = await client.GetAsync(uri);
        var json = await response.Content.ReadAsStringAsync();
        var bundle = Parse<Bundle>(json);

        AssertFhirResponse(response, HttpStatusCode.OK);
        Assert.Equal(Bundle.BundleType.Searchset, bundle.Type);
        Assert.Equal(2, bundle.Total);
        Assert.Equal(2, bundle.Entry.Count);
        foreach (var request in bundle.Entry.Select(entry => Assert.IsType<SupplyRequest>(entry.Resource)))
        {
            Assert.Contains(
                request.Identifier,
                identifier => identifier.System == FhirResourceMapper.RequisitionNoSystem &&
                              identifier.Value == requisition.RequisitionNo);
        }

        _output.WriteLine("T1 Bundle JSON:");
        _output.WriteLine(json);
        AssertValid(bundle, "T1 Bundle");
        foreach (var request in bundle.Entry.Select(entry => Assert.IsType<SupplyRequest>(entry.Resource)))
        {
            AssertValid(request, $"T1 SupplyRequest/{request.Id}");
        }
    }

    [Fact]
    public async Task Validator_rejects_missing_required_quantity_then_accepts_restored_resource()
    {
        var request = new SupplyRequest
        {
            Id = "validator-probe",
            Identifier = [new Identifier(FhirResourceMapper.RequisitionLineSystem, "validator-probe")],
            Status = SupplyRequest.SupplyRequestStatus.Active,
            Item = new CodeableConcept(FhirResourceMapper.ItemCodeSystem, "PROBE", "驗證探針"),
            Quantity = new Quantity(1, "ea"),
        };

        request.Quantity = null!;
        Assert.Null(request.Quantity); // 先證明突變真的進了送給驗證器的同一個 POCO。
        var invalidOutcome = NewValidator().Validate(request);
        var errors = Errors(invalidOutcome);

        Assert.NotEmpty(errors);
        _output.WriteLine("T2 mutated resource: SupplyRequest.quantity=<null>");
        foreach (var error in errors)
        {
            _output.WriteLine("T2 validator error: " + Describe(error));
        }

        request.Quantity = new Quantity(1, "ea");
        Assert.NotNull(request.Quantity);
        AssertValid(request, "T2 restored SupplyRequest");
    }

    [Fact]
    public async Task All_six_database_statuses_are_observed_through_the_API_with_declared_information_loss()
    {
        await using var scenario = await FhirTestScenario.CreateAsync(itemCount: 1);
        var expected = new Dictionary<string, SupplyRequest.SupplyRequestStatus>(StringComparer.Ordinal)
        {
            ["Draft"] = SupplyRequest.SupplyRequestStatus.Draft,
            ["PendingApproval"] = SupplyRequest.SupplyRequestStatus.Active,
            ["Approved"] = SupplyRequest.SupplyRequestStatus.Active,
            ["Rejected"] = SupplyRequest.SupplyRequestStatus.Cancelled,
            ["Issued"] = SupplyRequest.SupplyRequestStatus.Completed,
            ["Closed"] = SupplyRequest.SupplyRequestStatus.Completed,
        };
        using var client = await CreateAuthenticatedClientAsync(TestIdentitySeeder.StorekeeperEmail);

        foreach (var (databaseStatus, fhirStatus) in expected)
        {
            var requisition = await scenario.CreateRequisitionAsync(databaseStatus, (0, 1));
            var response = await client.GetAsync($"/fhir/SupplyRequest/{requisition.LineIds.Single()}");
            var request = Parse<SupplyRequest>(await response.Content.ReadAsStringAsync());

            AssertFhirResponse(response, HttpStatusCode.OK);
            Assert.Equal(fhirStatus, request.Status);
            AssertValid(request, $"T3 {databaseStatus}");
            _output.WriteLine($"T3 API mapping: {databaseStatus} -> {request.StatusElement?.Value}");
        }

        var unknown = Assert.Throws<InvalidOperationException>(
            () => FhirResourceMapper.ToSupplyRequestStatus("UnexpectedStatus"));
        Assert.Contains("UnexpectedStatus", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Based_on_search_preserves_two_FEFO_allocations_as_contained_Devices()
    {
        await using var scenario = await FhirTestScenario.CreateAsync(itemCount: 1);
        var requisition = await scenario.CreateRequisitionAsync("Approved", (0, 5));
        var allocation = await scenario.IssueAcrossTwoLotsAsync(requisition.LineIds.Single(), 0, 5);
        using var client = await CreateAuthenticatedClientAsync(TestIdentitySeeder.StorekeeperEmail);

        var response = await client.GetAsync(
            $"/fhir/SupplyDelivery?based-on=SupplyRequest/{requisition.LineIds.Single()}");
        var json = await response.Content.ReadAsStringAsync();
        var bundle = Parse<Bundle>(json);

        AssertFhirResponse(response, HttpStatusCode.OK);
        Assert.Equal(2, bundle.Total);
        var deliveries = bundle.Entry.Select(entry => Assert.IsType<SupplyDelivery>(entry.Resource)).ToList();
        Assert.Equal(2, deliveries.Count);
        Assert.Equal(allocation.LotNumbers, deliveries.Select(DeviceOf).Select(device => device.LotNumber!).ToList());
        Assert.Equal(
            allocation.ExpiryDates.Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            deliveries.Select(DeviceOf).Select(device => device.ExpirationDate!));
        Assert.Equal([2m, 3m], deliveries.Select(delivery => delivery.SuppliedItem!.Quantity!.Value));
        Assert.All(deliveries, delivery =>
            Assert.Equal($"SupplyRequest/{requisition.LineIds.Single()}", delivery.BasedOn.Single().Reference));

        _output.WriteLine("T4 SupplyDelivery search JSON:");
        _output.WriteLine(json);
        AssertValid(bundle, "T4 Bundle");
        foreach (var delivery in deliveries)
        {
            AssertValid(delivery, $"T4 SupplyDelivery/{delivery.Id}");
        }
    }

    [Fact]
    public async Task Fhir_authorization_and_not_found_errors_use_FHIR_HTTP_semantics()
    {
        await using var scenario = await FhirTestScenario.CreateAsync(itemCount: 1);
        var requisition = await scenario.CreateRequisitionAsync("Draft", (0, 1));
        var path = $"/fhir/SupplyRequest/{requisition.LineIds.Single()}";

        using var anonymous = CreateClient();
        var unauthorized = await anonymous.GetAsync(path);
        var unauthorizedBody = await unauthorized.Content.ReadAsStringAsync();
        AssertFhirResponse(unauthorized, HttpStatusCode.Unauthorized);
        var unauthorizedOutcome = Assert.IsType<OperationOutcome>(Parse<Resource>(unauthorizedBody));
        AssertValid(unauthorizedOutcome, "T5 unauthenticated OperationOutcome");

        using var requester = await CreateAuthenticatedClientAsync(TestIdentitySeeder.RequesterEmail);
        var forbidden = await requester.GetAsync(path);
        AssertFhirResponse(forbidden, HttpStatusCode.Forbidden);
        var forbiddenOutcome = Assert.IsType<OperationOutcome>(
            Parse<Resource>(await forbidden.Content.ReadAsStringAsync()));
        AssertValid(forbiddenOutcome, "T5 forbidden OperationOutcome");

        using var keeper = await CreateAuthenticatedClientAsync(TestIdentitySeeder.StorekeeperEmail);
        var allowed = await keeper.GetAsync(path);
        AssertFhirResponse(allowed, HttpStatusCode.OK);

        using var administrator = await CreateAuthenticatedClientAsync(TestIdentitySeeder.AdministratorEmail);
        var administratorAllowed = await administrator.GetAsync(path);
        AssertFhirResponse(administratorAllowed, HttpStatusCode.OK);

        var metadata = await anonymous.GetAsync("/fhir/metadata");
        AssertFhirResponse(metadata, HttpStatusCode.OK);

        var missing = await keeper.GetAsync("/fhir/SupplyRequest/9223372036854775807");
        var missingOutcome = Parse<OperationOutcome>(await missing.Content.ReadAsStringAsync());
        AssertFhirResponse(missing, HttpStatusCode.NotFound);
        Assert.Equal(OperationOutcome.IssueType.NotFound, missingOutcome.Issue.Single().Code);
        AssertValid(missingOutcome, "not-found OperationOutcome");

        _output.WriteLine($"T5 anonymous HTTP {(int)unauthorized.StatusCode}; body[0..200]={unauthorizedBody[..Math.Min(200, unauthorizedBody.Length)]}");
    }

    [Fact]
    public async Task Malformed_search_parameters_return_valid_OperationOutcomes()
    {
        using var keeper = await CreateAuthenticatedClientAsync(TestIdentitySeeder.StorekeeperEmail);

        var malformedIdentifier = await keeper.GetAsync("/fhir/SupplyRequest?identifier=missing-system-separator");
        var identifierOutcome = Parse<OperationOutcome>(await malformedIdentifier.Content.ReadAsStringAsync());
        AssertFhirResponse(malformedIdentifier, HttpStatusCode.BadRequest);
        Assert.Equal(OperationOutcome.IssueType.Invalid, identifierOutcome.Issue.Single().Code);
        AssertValid(identifierOutcome, "bad identifier OperationOutcome");

        var malformedBasedOn = await keeper.GetAsync("/fhir/SupplyDelivery?based-on=SupplyRequest/not-an-id");
        var basedOnOutcome = Parse<OperationOutcome>(await malformedBasedOn.Content.ReadAsStringAsync());
        AssertFhirResponse(malformedBasedOn, HttpStatusCode.BadRequest);
        Assert.Equal(OperationOutcome.IssueType.Invalid, basedOnOutcome.Issue.Single().Code);
        AssertValid(basedOnOutcome, "bad based-on OperationOutcome");
    }

    [Fact]
    public async Task CapabilityStatement_declares_exactly_the_working_read_only_surface()
    {
        await using var scenario = await FhirTestScenario.CreateAsync(itemCount: 1);
        var requisition = await scenario.CreateRequisitionAsync("Approved", (0, 5));
        var allocation = await scenario.IssueAcrossTwoLotsAsync(requisition.LineIds.Single(), 0, 5);
        using var anonymous = CreateClient();
        using var keeper = await CreateAuthenticatedClientAsync(TestIdentitySeeder.StorekeeperEmail);

        var metadataResponse = await anonymous.GetAsync("/fhir/metadata");
        var metadataJson = await metadataResponse.Content.ReadAsStringAsync();
        var capability = Parse<CapabilityStatement>(metadataJson);
        AssertFhirResponse(metadataResponse, HttpStatusCode.OK);
        Assert.Equal(FHIRVersion.N4_0_1, capability.FhirVersion);
        Assert.Equal(["json"], capability.Format);

        var resources = capability.Rest.Single().Resource.ToDictionary(
            resource => resource.Type ?? throw new InvalidOperationException("CapabilityStatement resource.type 不可為空"),
            StringComparer.Ordinal);
        Assert.Equal(["SupplyDelivery", "SupplyRequest"], resources.Keys.Order(StringComparer.Ordinal));
        AssertCapabilityResource(resources["SupplyRequest"], "identifier", SearchParamType.Token);
        AssertCapabilityResource(resources["SupplyDelivery"], "based-on", SearchParamType.Reference);

        var declaredCalls = new[]
        {
            await keeper.GetAsync($"/fhir/SupplyRequest/{requisition.LineIds.Single()}"),
            await keeper.GetAsync("/fhir/SupplyRequest?identifier=" + Uri.EscapeDataString(
                $"{FhirResourceMapper.RequisitionNoSystem}|{requisition.RequisitionNo}")),
            await keeper.GetAsync($"/fhir/SupplyDelivery/{allocation.AllocationIds[0]}"),
            await keeper.GetAsync($"/fhir/SupplyDelivery?based-on=SupplyRequest/{requisition.LineIds.Single()}"),
        };
        Assert.All(declaredCalls, response => AssertFhirResponse(response, HttpStatusCode.OK));

        var unsupported = await keeper.PostAsync("/fhir/SupplyRequest", new StringContent(string.Empty));
        var unsupportedBody = await unsupported.Content.ReadAsStringAsync();
        var unsupportedOutcome = Parse<OperationOutcome>(unsupportedBody);
        AssertFhirResponse(unsupported, HttpStatusCode.MethodNotAllowed);
        Assert.Equal(OperationOutcome.IssueType.NotSupported, unsupportedOutcome.Issue.Single().Code);
        AssertValid(unsupportedOutcome, "T6 unsupported-interaction OperationOutcome");

        _output.WriteLine("T6 CapabilityStatement JSON:");
        _output.WriteLine(metadataJson);
        _output.WriteLine("T6 declared calls: SupplyRequest read=200; SupplyRequest search-type=200; SupplyDelivery read=200; SupplyDelivery search-type=200");
        _output.WriteLine($"T6 undeclared POST /fhir/SupplyRequest: HTTP {(int)unsupported.StatusCode}; {unsupportedBody}");
        AssertValid(capability, "T6 CapabilityStatement");
    }

    private static void AssertCapabilityResource(
        CapabilityStatement.ResourceComponent resource,
        string searchName,
        SearchParamType searchType)
    {
        Assert.Equal(
            [CapabilityStatement.TypeRestfulInteraction.Read, CapabilityStatement.TypeRestfulInteraction.SearchType],
            resource.Interaction.Select(interaction => interaction.Code));
        var search = Assert.Single(resource.SearchParam);
        Assert.Equal(searchName, search.Name);
        Assert.Equal(searchType, search.Type);
    }

    private static Device DeviceOf(SupplyDelivery delivery)
    {
        var device = Assert.IsType<Device>(Assert.Single(delivery.Contained));
        Assert.Equal($"#{device.Id}", Assert.IsType<ResourceReference>(delivery.SuppliedItem!.Item).Reference);
        return device;
    }

    private void AssertValid(Resource resource, string label)
    {
        var outcome = NewValidator().Validate(resource);
        var errors = Errors(outcome);
        var warnings = outcome.Issue.Where(issue => issue.Severity == OperationOutcome.IssueSeverity.Warning).ToList();

        _output.WriteLine($"{label} validator: error={errors.Count}, warning={warnings.Count}");
        foreach (var warning in warnings)
        {
            _output.WriteLine($"{label} warning: {Describe(warning)}");
        }

        foreach (var error in errors)
        {
            _output.WriteLine($"{label} error: {Describe(error)}");
        }

        Assert.True(errors.Count == 0, $"{label} validation errors: {string.Join(" | ", errors.Select(Describe))}");
    }

    private static Validator NewValidator()
    {
        var specificationZip = Path.Combine(AppContext.BaseDirectory, "specification.zip");
        Assert.True(File.Exists(specificationZip), $"離線核心定義不存在：{specificationZip}");
        var source = new ZipSource(specificationZip);
        var resolver = new CachedResolver(source);
        var terminology = new LocalTerminologyService(resolver, new ValueSetExpanderSettings());
        return new Validator(resolver, terminology, null!, null!, null!);
    }

    private static List<OperationOutcome.IssueComponent> Errors(OperationOutcome outcome)
        => outcome.Issue.Where(issue =>
            issue.Severity is OperationOutcome.IssueSeverity.Error or OperationOutcome.IssueSeverity.Fatal).ToList();

    private static string Describe(OperationOutcome.IssueComponent issue)
        => $"{issue.Severity}/{issue.Code}: details={issue.Details?.Text}; diagnostics={issue.Diagnostics}; " +
           $"expression={string.Join(",", issue.Expression)}; location={string.Join(",", issue.Location)}";

    private static T Parse<T>(string json)
        where T : Resource
        => FhirJsonDeserializer.DEFAULT.Deserialize<T>(json);

    private static void AssertFhirResponse(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/fhir+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet, ignoreCase: true);
    }

    private HttpClient CreateClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string email)
    {
        var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, email);
        return client;
    }
}

internal sealed class FhirTestScenario : IAsyncDisposable
{
    private const string TestActor = "itest-fhir";
    private static readonly DateOnly Today = TestBusinessCalendar.SystemToday;
    private readonly string _suffix;
    private readonly List<long> _requisitionIds = [];

    private FhirTestScenario(string suffix)
    {
        _suffix = suffix;
    }

    public long DepartmentId { get; private set; }
    public IReadOnlyList<long> ItemIds { get; private set; } = [];

    public static async Task<FhirTestScenario> CreateAsync(int itemCount)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var scenario = new FhirTestScenario(suffix);
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync(
            "INSERT INTO departments (department_code, department_name, created_by) VALUES (:code, :name, :actor)",
            new { code = "F" + suffix, name = "FHIR 測試科室 " + suffix, actor = TestActor });
        scenario.DepartmentId = await ScalarIdAsync(
            connection,
            "SELECT department_id FROM departments WHERE department_code = :code",
            new { code = "F" + suffix });

        var itemIds = new List<long>();
        for (var i = 0; i < itemCount; i++)
        {
            var code = string.Create(CultureInfo.InvariantCulture, $"FHIR-{suffix}-{i:D2}");
            await connection.ExecuteAsync(
                """
                INSERT INTO items (item_code, item_name, specification, unit_of_measure, safety_stock_qty, created_by)
                VALUES (:code, :name, :specification, 'ea', 0, :actor)
                """,
                new { code, name = "FHIR 測試品項 " + i, specification = "規格 " + i, actor = TestActor });
            itemIds.Add(await ScalarIdAsync(
                connection,
                "SELECT item_id FROM items WHERE item_code = :code",
                new { code }));
        }

        scenario.ItemIds = itemIds;
        return scenario;
    }

    public async Task<TestRequisition> CreateRequisitionAsync(
        string status,
        params (int ItemIndex, int Quantity)[] lines)
    {
        var no = $"FHIR-{_suffix}-{_requisitionIds.Count:D2}";
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO requisitions (requisition_no, department_id, status, rejection_reason, created_by)
            VALUES (:no, :departmentId, :status, :rejectionReason, :actor)
            """,
            new
            {
                no,
                departmentId = DepartmentId,
                status,
                rejectionReason = status == "Rejected" ? "FHIR 測試駁回原因" : null,
                actor = TestActor,
            });
        var requisitionId = await ScalarIdAsync(
            connection,
            "SELECT requisition_id FROM requisitions WHERE requisition_no = :no",
            new { no });
        _requisitionIds.Add(requisitionId);

        var lineIds = new List<long>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            await connection.ExecuteAsync(
                """
                INSERT INTO requisition_lines (requisition_id, line_no, item_id, quantity)
                VALUES (:requisitionId, :lineNo, :itemId, :quantity)
                """,
                new
                {
                    requisitionId,
                    lineNo = i + 1,
                    itemId = ItemIds[line.ItemIndex],
                    line.Quantity,
                });
            lineIds.Add(await ScalarIdAsync(
                connection,
                "SELECT requisition_line_id FROM requisition_lines WHERE requisition_id = :requisitionId AND line_no = :lineNo",
                new { requisitionId, lineNo = i + 1 }));
        }

        return new TestRequisition(requisitionId, no, lineIds);
    }

    public async Task<TestAllocation> IssueAcrossTwoLotsAsync(
        long requisitionLineId,
        int itemIndex,
        int requestedQuantity)
    {
        var itemId = ItemIds[itemIndex];
        var lotNumbers = new[] { $"FHIR-EARLY-{_suffix}", $"FHIR-LATE-{_suffix}" };
        var expiryDates = new[] { Today.AddDays(30), Today.AddDays(60) };
        await using (var setup = new OracleConnection(OracleTestDatabase.ConnectionString))
        {
            await setup.OpenAsync();
            for (var i = 0; i < lotNumbers.Length; i++)
            {
                await setup.ExecuteAsync(
                    """
                    INSERT INTO stock_lots
                        (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
                    VALUES
                        (:itemId, :lotNumber, :expiryDate, :quantity, :storageLocation, :actor)
                    """,
                    new
                    {
                        itemId,
                        lotNumber = lotNumbers[i],
                        expiryDate = expiryDates[i].ToDateTime(TimeOnly.MinValue),
                        quantity = i == 0 ? 2 : 10,
                        storageLocation = "FHIR-" + i,
                        actor = TestActor,
                    });
            }
        }

        await using (var issueConnection = new OracleConnection(OracleTestDatabase.ConnectionString))
        {
            var result = await new StockIssueService(issueConnection).IssueAsync(
                requisitionLineId,
                itemId,
                requestedQuantity,
                Today,
                TestActor);
            Assert.True(result.IsSuccess);
        }

        await using var read = new OracleConnection(OracleTestDatabase.ConnectionString);
        var rows = (await read.QueryAsync<AllocationIdRow>(
            """
            SELECT a.issue_allocation_id AS IssueAllocationId, l.lot_number AS LotNumber
            FROM issue_allocations a
            INNER JOIN stock_lots l ON l.stock_lot_id = a.stock_lot_id
            WHERE a.requisition_line_id = :requisitionLineId
            ORDER BY a.expiry_date_at_issue, l.lot_number
            """,
            new { requisitionLineId })).ToList();
        Assert.Equal(lotNumbers, rows.Select(row => row.LotNumber));
        return new TestAllocation(
            rows.Select(row => decimal.ToInt64(row.IssueAllocationId)).ToList(),
            lotNumbers,
            expiryDates);
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var itemIds = ItemIds.ToList();

        if (_requisitionIds.Count > 0)
        {
            await connection.ExecuteAsync(
                "DELETE FROM audit_logs WHERE actor = :actor AND entity_id IN :entityIds",
                new
                {
                    actor = TestActor,
                    entityIds = _requisitionIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList(),
                });
            await connection.ExecuteAsync(
                """
                DELETE FROM issue_allocations
                WHERE requisition_line_id IN
                    (SELECT requisition_line_id FROM requisition_lines WHERE requisition_id IN :requisitionIds)
                """,
                new { requisitionIds = _requisitionIds });
            await connection.ExecuteAsync(
                "DELETE FROM requisition_lines WHERE requisition_id IN :requisitionIds",
                new { requisitionIds = _requisitionIds });
            await connection.ExecuteAsync(
                "DELETE FROM requisitions WHERE requisition_id IN :requisitionIds",
                new { requisitionIds = _requisitionIds });
        }

        if (itemIds.Count > 0)
        {
            await connection.ExecuteAsync("DELETE FROM stock_lots WHERE item_id IN :itemIds", new { itemIds });
            await connection.ExecuteAsync("DELETE FROM items WHERE item_id IN :itemIds", new { itemIds });
        }

        await connection.ExecuteAsync(
            "DELETE FROM departments WHERE department_id = :departmentId",
            new { departmentId = DepartmentId });
        await connection.ExecuteAsync("COMMIT");
    }

    private static async Task<long> ScalarIdAsync(OracleConnection connection, string sql, object parameters)
    {
        var value = await connection.ExecuteScalarAsync<decimal?>(sql, parameters);
        return value is null ? throw new InvalidOperationException($"查無測試資料：{sql}") : decimal.ToInt64(value.Value);
    }

    private sealed class AllocationIdRow
    {
        public decimal IssueAllocationId { get; set; }
        public string LotNumber { get; set; } = string.Empty;
    }
}

internal sealed record TestRequisition(long RequisitionId, string RequisitionNo, IReadOnlyList<long> LineIds);

internal sealed record TestAllocation(
    IReadOnlyList<long> AllocationIds,
    IReadOnlyList<string> LotNumbers,
    IReadOnlyList<DateOnly> ExpiryDates);
