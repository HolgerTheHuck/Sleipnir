// Fixture for the discovery-service navigation test: server DTOs carrying the SERVER-side
// [SleipnirNavigation] (SleipnirCommon.Attribute) — the producer half of the one-declaration pipeline.
// Distinct from the client attribute in Sleipnir.Client.Linq. Both DTOs are in the test assembly →
// expanded by Weg C, so the nav edge is serialized into the discovery JSON.
using SleipnirCommon.Attribute;
using SleipnirCore.Attributes;

namespace SleipnirTests.Fixtures;

public class NavRootDto
{
    public int Id { get; set; }
    public int? OwnerId { get; set; }
    public string Plain { get; set; } = "";

    /// <summary>Reference navigation: OwnerId → NavOwnerDto.Id, fetched via NavFetch.GetOwners.</summary>
    [SleipnirNavigation(Fetch = "NavFetch.GetOwners", Key = "ownerId", ChildKey = "id", Param = "ownerIds")]
    public NavOwnerDto? Owner { get; set; }
}

public class NavOwnerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

[SleipnirController("NavFetch")]
public class NavFetchController
{
    [SleipnirMethod("SelectRoots")]
    public List<NavRootDto> SelectRoots() => new();

    [SleipnirMethod("GetOwners")]
    public List<NavOwnerDto> GetOwners(List<int> ownerIds) => new();
}