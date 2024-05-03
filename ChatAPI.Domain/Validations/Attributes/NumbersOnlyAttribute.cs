using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace ChatAPI.Domain.Validations.Attributes;
public class NumbersOnlyAttribute : ValidationAttribute
{
	private static readonly string pattern = @"^[0-9]+$";

	protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
	{
		if (value is null)
			return new ValidationResult("The value cannot be null.");

		string valueAsString = value.ToString()!;

		return IsNumbersOnly(valueAsString)
			? ValidationResult.Success
			: new ValidationResult("You can enter only numbers.");
	}

	private static bool IsNumbersOnly(string phone) =>
		Regex.IsMatch(phone, pattern);
}
