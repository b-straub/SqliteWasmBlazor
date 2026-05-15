using MessagePack;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

[MessagePackObject]
public class CryptoTestItemDto
{
    [Key(0)] public Guid Id { get; set; }
    [Key(1)] public string Title { get; set; } = "";
    [Key(2)] public string Description { get; set; } = "";
    [Key(3)] public decimal Price { get; set; }
    [Key(4)] public bool IsBought { get; set; }
    [Key(5)] public int SharingScope { get; set; }
    [Key(6)] public string SharingId { get; set; } = "";
    [Key(7)] public DateTime UpdatedAt { get; set; }
    [Key(8)] public bool IsDeleted { get; set; }
    [Key(9)] public DateTime? DeletedAt { get; set; }
}
