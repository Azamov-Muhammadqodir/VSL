using Microsoft.AspNetCore.Components;

namespace Components;

public partial class Greeting
{
    [Parameter]
    public string Name { get; set; } = "world";
}
