using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Database.Entities;

public class CountryCodesEntity
{
	public CountryCodesEntity(string country, string code, string icon)
	{
		Country = country;
		Code = code;
		Icon = icon;
	}

	[Key]
	[Required]
	public string Country { get; set; }

	[Required]
	public string Code { get; set; }

	[Required]
	public string Icon { get; set; }
}