using System.ComponentModel.DataAnnotations;

namespace RenzoBackend.Models.Database;

public class SettingEntity
{
    [Key]
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}