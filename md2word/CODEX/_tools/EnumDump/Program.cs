using System.Reflection;
using DocumentFormat.OpenXml.Math;

static void DumpEnum(string name, Type type)
{
    if (type.IsEnum)
    {
        Console.WriteLine($"{name}: enum [{string.Join(", ", Enum.GetNames(type))}]");
    }
    else
    {
        Console.WriteLine($"{name}: TYPE {type.FullName} (enum={type.IsEnum})");
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            Console.WriteLine($"    static field {f.Name} : {f.FieldType.Name} = {f.GetValue(null)}");
        }

        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            Console.WriteLine($"    static prop  {p.Name} : {p.PropertyType.Name}");
        }

        if (type.GetProperty("Val") is { } v)
        {
            Console.WriteLine($"    .Val : {v.PropertyType.FullName} (enum={v.PropertyType.IsEnum})");
            if (v.PropertyType.IsEnum)
            {
                Console.WriteLine($"    members: {string.Join(", ", Enum.GetNames(v.PropertyType))}");
            }
        }
    }
}

foreach (var (name, type) in new (string, Type)[]
{
    ("LimitLocationValues", typeof(LimitLocationValues)),
    ("BooleanValues", typeof(BooleanValues)),
    ("FractionTypeValues", typeof(FractionTypeValues)),
    ("StyleValues", typeof(StyleValues)),
    ("TopBottomType", typeof(TopBottomType)),
    ("Position", typeof(Position)),
    ("LimitLocation", typeof(LimitLocation)),
    ("HideSubArgument", typeof(HideSubArgument)),
    ("HideDegree", typeof(HideDegree)),
    ("AccentChar", typeof(AccentChar)),
    ("BeginChar", typeof(BeginChar)),
    ("FractionType", typeof(FractionType)),
})
{
    DumpEnum(name, type);
}

Console.WriteLine();
Console.WriteLine("Position.Val 赋值用的实际属性：");
foreach (var p in typeof(Position).GetProperties(BindingFlags.Public | BindingFlags.Instance))
{
    Console.WriteLine($"   {p.Name} : {p.PropertyType.FullName}");
}

