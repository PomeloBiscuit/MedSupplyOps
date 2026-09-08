using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed class AuthorizationMetadataTests
{
    private readonly ITestOutputHelper _output;

    public AuthorizationMetadataTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Every_public_controller_action_has_an_explicit_policy_or_allow_anonymous()
    {
        var actions = typeof(Program).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName && method.GetCustomAttribute<NonActionAttribute>() is null)
                .Select(method => new { Controller = type, Method = method }))
            .OrderBy(action => action.Controller.Name, StringComparer.Ordinal)
            .ThenBy(action => action.Method.Name, StringComparer.Ordinal)
            .ThenBy(action => action.Method.MetadataToken)
            .ToList();

        Assert.NotEmpty(actions);
        foreach (var action in actions)
        {
            var allowAnonymous = action.Method.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
            var policies = action.Method.GetCustomAttributes<AuthorizeAttribute>()
                .Select(attribute => attribute.Policy)
                .Where(policy => !string.IsNullOrWhiteSpace(policy))
                .Cast<string>()
                .ToList();
            var coverage = allowAnonymous ? "AllowAnonymous" : string.Join(",", policies);
            _output.WriteLine(
                $"{action.Controller.Name}.{action.Method.Name} | {FriendlyTypeName(action.Method.ReturnType)} | {coverage}");

            Assert.True(
                allowAnonymous || policies.Count > 0,
                $"{action.Controller.Name}.{action.Method.Name} 是公開 Action，但沒有明確 Policy 或 [AllowAnonymous]。");
        }

        Assert.Contains(actions, action =>
            action.Controller.Name == "InventoryApiController" && IsTaskOfActionResult(action.Method.ReturnType));
        Assert.Contains(actions, action =>
            action.Controller.Name == "ItemsApiController" && IsTaskOfActionResult(action.Method.ReturnType));
    }

    private static bool IsTaskOfActionResult(Type returnType)
        => returnType.IsGenericType &&
           returnType.GetGenericTypeDefinition() == typeof(Task<>) &&
           returnType.GetGenericArguments()[0].IsGenericType &&
           returnType.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(ActionResult<>);

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
