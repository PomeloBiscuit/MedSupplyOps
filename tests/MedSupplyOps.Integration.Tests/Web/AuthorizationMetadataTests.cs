using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>
/// 每一個 Controller Action 都必須被明確分類：要嘛有授權 Policy，要嘛在「刻意公開」的白名單上。
///
/// ★ 這支測試改過一次，原因值得記下來。
///
/// 第一版是用**反射**去看方法上的 <c>[Authorize]</c> / <c>[AllowAnonymous]</c> 屬性。
/// 它對「現在」的程式碼給出正確答案，但有兩個看不見的洞：
///
/// 1. <b>Controller 類別層級的 <c>[AllowAnonymous]</c> 會壓過方法層級的 <c>[Authorize]</c></b>。
///    反射只看方法，所以它會照樣印出 Policy 名稱並通過 —— 而那個端點實際上是公開的。
/// 2. 反射看的是「原始碼上寫了什麼」，不是「執行期真正生效的是什麼」。
///    慣例（convention）、過濾器、`MapXxx().RequireAuthorization()` 這類接線它一概看不到。
///
/// 現在改成從 <see cref="EndpointDataSource"/> 讀端點的實際 metadata ——
/// 那正是 <c>AuthorizationMiddleware</c> 在執行期評估的同一份資料。
///
/// ★ 另一個關鍵改動：**公開端點採白名單**。
/// 只要求「有 Policy 或有 AllowAnonymous」是不夠的 ——
/// 那樣任何人只要加一個 <c>[AllowAnonymous]</c> 就能讓端點變公開而測試依舊全綠。
/// 現在把端點標成公開，一定要同時改下面那份清單，
/// 也就是說**「把某個東西變成公開」必須是一個有人看得見的決定**。
/// </summary>
public sealed class AuthorizationMetadataTests
    : IClassFixture<ApplicationStartupSmokeTests.ProductionLikeFactory>
{
    /// <summary>
    /// 刻意公開的端點。**新增到這份清單，等於做了一個資安決定，請寫清楚理由。**
    /// </summary>
    private static readonly HashSet<string> DeliberatelyPublic = new(StringComparer.Ordinal)
    {
        "AccountController.Login",        // 沒登入的人才需要它
        "AccountController.Register",     // 同上
        "AccountController.AccessDenied", // 權限不足時的落地頁，登入與否都要看得到
        "FhirController.Metadata",        // FHIR 客戶端先讀能力宣告，再決定如何驗證與呼叫
    };

    private readonly ApplicationStartupSmokeTests.ProductionLikeFactory _factory;
    private readonly ITestOutputHelper _output;

    public AuthorizationMetadataTests(
        ApplicationStartupSmokeTests.ProductionLikeFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public void Every_routed_controller_action_is_explicitly_classified()
    {
        // 先建立 Client 逼 Host 真的啟動，路由表才會被建出來。
        _ = _factory.CreateClient();

        var actions = GetControllerEndpoints()
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Endpoint.DisplayName, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(actions);

        foreach (var (name, endpoint, descriptor) in actions)
        {
            // ★ 這兩行就是 AuthorizationMiddleware 在執行期做的事。
            //   用同一份 metadata 判斷，才不會發生「原始碼看起來有保護、實際上沒有」。
            var allowAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(data => data.Policy)
                .Where(policy => !string.IsNullOrWhiteSpace(policy))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var coverage = allowAnonymous ? "AllowAnonymous" : string.Join(",", policies);
            _output.WriteLine($"{name} | {FriendlyTypeName(descriptor.MethodInfo.ReturnType)} | {coverage}");

            if (allowAnonymous)
            {
                Assert.True(
                    DeliberatelyPublic.Contains(name),
                    $"{name} 在執行期是**公開**的（端點帶有 AllowAnonymous metadata），但它不在刻意公開的清單上。" +
                    "若這是有意的，請把它加進 DeliberatelyPublic 並寫下理由；" +
                    "若不是，請找出是誰讓它變公開的 —— 注意 Controller 類別層級的 [AllowAnonymous] 會壓過方法層級的 [Authorize]。");
                continue;
            }

            Assert.True(
                policies.Count > 0,
                $"{name} 是公開路由的 Action，但執行期的授權 metadata 裡沒有任何 Policy。" +
                "每個 Action 都必須明確分類：要嘛給 Policy，要嘛標 [AllowAnonymous] 並登記到 DeliberatelyPublic。");
        }
    }

    /// <summary>
    /// 白名單本身也要防呆：清單上列了、但實際上根本沒有這個端點的話，
    /// 它就是一條沒有人會發現的死條目 —— 而死條目會讓下一個人以為某個東西「已經被審過了」。
    /// </summary>
    [Fact]
    public void Deliberately_public_list_has_no_stale_entries()
    {
        _ = _factory.CreateClient();

        var names = GetControllerEndpoints().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var stale = DeliberatelyPublic.Where(entry => !names.Contains(entry)).ToList();

        Assert.True(stale.Count == 0, $"DeliberatelyPublic 有已經不存在的端點：{string.Join(", ", stale)}");
    }

    /// <summary>
    /// ★ 兩支 Web API 必須在列舉結果裡。
    ///
    /// 它們回傳的是 <c>Task&lt;ActionResult&lt;T&gt;&gt;</c> 而不是 <c>IActionResult</c>。
    /// 任何「用回傳型別篩選 Action」的寫法都會整組漏掉它們，
    /// 而漏掉之後**清單看起來仍然很完整** —— 沒有人會發現 API 從來沒被檢查過。
    /// </summary>
    [Fact]
    public void Api_controllers_returning_ActionResult_of_T_are_included()
    {
        _ = _factory.CreateClient();

        var byController = GetControllerEndpoints()
            .GroupBy(item => item.Descriptor.ControllerTypeInfo.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var controller in new[] { "InventoryApiController", "ItemsApiController" })
        {
            Assert.True(byController.ContainsKey(controller), $"{controller} 不在端點列舉結果裡。");
            Assert.Contains(byController[controller], item => IsTaskOfActionResult(item.Descriptor.MethodInfo.ReturnType));
        }
    }

    /// <summary>
    /// 只列舉 Controller Action。
    /// 非 Controller 的端點（靜態檔等）不在這裡檢查 ——
    /// 它們由 <c>Program.cs</c> 的 fallback policy 兜底，而靜態檔是明確 <c>.AllowAnonymous()</c> 的。
    /// </summary>
    private List<(string Name, Endpoint Endpoint, ControllerActionDescriptor Descriptor)> GetControllerEndpoints()
    {
        var sources = _factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();

        return sources
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => (endpoint, descriptor: endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()))
            .Where(item => item.descriptor is not null)
            .Select(item => (
                Name: $"{item.descriptor!.ControllerTypeInfo.Name}.{item.descriptor.MethodInfo.Name}",
                Endpoint: item.endpoint,
                Descriptor: item.descriptor))
            .ToList();
    }

    private static bool IsTaskOfActionResult(Type returnType)
        => returnType.IsGenericType &&
           returnType.GetGenericTypeDefinition() == typeof(Task<>) &&
           returnType.GetGenericArguments()[0].IsGenericType &&
           returnType.GetGenericArguments()[0].GetGenericTypeDefinition() ==
               typeof(Microsoft.AspNetCore.Mvc.ActionResult<>);

    private static string FriendlyTypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(",", type.GetGenericArguments().Select(FriendlyTypeName))}>";
    }
}
