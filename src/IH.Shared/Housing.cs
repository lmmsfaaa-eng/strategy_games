namespace IH.Shared;

public enum HouseMaterial : byte { Wood, Clay, Stone, Concrete }

public sealed record HousingDef(HouseMaterial Material, string Id, string Name, string Tech, int Cost, int Places, string Notes)
{
    public string Item => "h:" + Id;
}

public static class Housing
{
    public const int BasePlaces = 4;
    public static readonly HousingDef[] Materials =
    {
        new(HouseMaterial.Wood, "wood", "Деревянный дом", "agriculture", 25, 2, "Горит. Действующая лесопилка удваивает скорость строительства."),
        new(HouseMaterial.Clay, "clay", "Глиняный дом", "pottery", 20, 2, "Сильный дождь может размыть дом."),
        new(HouseMaterial.Stone, "stone", "Каменный дом", "masonry", 45, 3, "Для строительства нужна действующая каменоломня державы. Устойчив к пожару и ливню."),
        new(HouseMaterial.Concrete, "concrete", "Бетонный дом", "construction", 75, 4, "Устойчив к пожару и ливню."),
    };

    public static HousingDef? Project(string item) => Array.Find(Materials, house => house.Item == item);
}
