using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using PcMieru.Core.Hardware.Model;
using PcMieru.Wire;

namespace PcMieru.Core.Tests;

/// <summary>Rules that keep five parallel branches from breaking each other's data shapes.</summary>
public class ContractRuleTests
{
    private static readonly Assembly CoreAssembly = typeof(Snapshot).Assembly;

    private static IEnumerable<Type> PublicCoreTypes() =>
        CoreAssembly.GetExportedTypes().Where(t => !t.IsGenericTypeDefinition);

    private static IEnumerable<Type> ValueArguments(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Value<>))
        {
            var arg = type.GetGenericArguments()[0];
            yield return arg;
            foreach (var inner in ValueArguments(arg))
            {
                yield return inner;
            }
        }
        else if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments().SelectMany(ValueArguments))
            {
                yield return arg;
            }
        }
    }

    [Fact]
    public void Every_T_inside_a_Value_is_resolvable_by_the_json_chain()
    {
        var types = PublicCoreTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .SelectMany(p => ValueArguments(p.PropertyType))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(types);

        var missing = new List<string>();
        foreach (var t in types)
        {
            try
            {
                PcMieruJson.GetTypeInfo(t);
            }
            catch (NotSupportedException)
            {
                missing.Add(t.Name);
            }
        }

        Assert.True(missing.Count == 0, "register in your area JSON context: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_public_enum_serializes_as_a_camel_case_string()
    {
        var enums = PublicCoreTypes().Concat(typeof(OperationCode).Assembly.GetExportedTypes()).Where(t => t.IsEnum && t != typeof(Language) && t != typeof(HelperOperation)).ToArray();
        Assert.NotEmpty(enums);
        var bad = enums
            .Where(e => e.GetCustomAttribute<JsonConverterAttribute>() is not { } a || !a.ConverterType!.Name.StartsWith("CamelCaseEnumConverter", StringComparison.Ordinal))
            .Select(e => e.Name)
            .ToArray();
        Assert.True(bad.Length == 0, "missing [JsonConverter(typeof(CamelCaseEnumConverter<...>))]: " + string.Join(", ", bad));
    }

    [Fact]
    public void Contract_records_are_immutable()
    {
        var records = PublicCoreTypes().Where(t => t.GetMethod("<Clone>$") is not null).ToArray();
        Assert.True(records.Length > 40);
        var mutable = new List<string>();
        foreach (var record in records)
        {
            foreach (var p in record.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var setter = p.SetMethod;
                if (setter is not null && !setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)))
                {
                    mutable.Add(record.Name + "." + p.Name);
                }
            }
        }

        Assert.True(mutable.Count == 0, "settable properties on contract records: " + string.Join(", ", mutable));
    }

    [Fact]
    public void Read_only_insights_has_exactly_the_seven_read_methods()
    {
        var names = typeof(IReadOnlyInsights).GetMethods().Select(m => m.Name).Order().ToArray();
        Assert.Equal(
            ["DiagnoseAsync", "GetHardwareAsync", "GetProcessAsync", "GetReportAsync", "GetStorageHealthAsync", "GetSystemSummaryAsync", "ListProcessesAsync"],
            names);
        Assert.Empty(typeof(IReadOnlyInsights).GetProperties());
        Assert.Empty(typeof(IReadOnlyInsights).GetInterfaces());
    }

    [Fact]
    public void The_group_and_icon_contracts_are_contracts_every_front_end_can_use()
    {
        // They started life inside the collection area; front ends program against the Contracts namespace only.
        foreach (var type in new[] { typeof(IProcessGroupSource), typeof(IProcessIconSource), typeof(ProcessGroup) })
        {
            Assert.Equal("PcMieru.Core", type.Namespace);
        }
    }

    [Fact]
    public void Value_status_zero_is_not_collected()
    {
        Assert.Equal(0, (int)ValueStatus.NotCollected);
    }

    [Fact]
    public void Operation_codes_are_stable_because_the_helper_returns_them_as_its_exit_code()
    {
        Assert.Equal(0, (int)OperationCode.Ok);
        Assert.Equal(1, (int)OperationCode.Error);
        Assert.Equal(2, (int)OperationCode.InvalidArguments);
        Assert.Equal(3, (int)OperationCode.NotFound);
        Assert.Equal(4, (int)OperationCode.Denied);
        Assert.Equal(5, (int)OperationCode.Protected);
        Assert.Equal(6, (int)OperationCode.MultipleMatches);
        Assert.Equal(7, (int)OperationCode.PartialSuccess);
        Assert.Equal(8, (int)OperationCode.Gone);
        Assert.Equal(9, (int)OperationCode.Cancelled);
        Assert.Equal(10, (int)OperationCode.Unsupported);
        Assert.Equal(64, (int)OperationCode.NotImplemented);
    }

    [Fact]
    public void Hardware_model_covers_every_category()
    {
        var info = typeof(HardwareInfo).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Contains("Cpu", info);
        Assert.Contains("Storage", info);
        Assert.True(Enum.GetValues<HardwareCategory>().Length >= 8);
    }
}
