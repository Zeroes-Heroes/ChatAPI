using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace ChatAPI.Domain.Validations.Attributes;
public class PhoneAttribute : ValidationAttribute
{
	private static readonly string pattern = @"^\+\d+$";

	protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
	{
		if (value is null)
			return new ValidationResult("The value cannot be null.");

		string phoneNumber = value.ToString()!;

		return IsValidPhoneNumber(phoneNumber)
			? ValidationResult.Success 
			: new ValidationResult("The phone number is not valid.");
	}

	private static bool IsValidPhoneNumber(string phone) =>
		Regex.IsMatch(phone, pattern);
}
