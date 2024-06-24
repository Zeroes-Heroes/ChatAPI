using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Application.Utilities;

namespace ChatAPI.Application.UseCases.Abstractions.Services
{
	public interface IUserService
	{
		Task<Result> Register(UserRegisterDTO dto);
		Task<Result<SecretLoginCodeDTO>> VerifySmsCode(VerifySmsCodeDTO code);
		Task<Result<UserLoginResponseDTO>> Login(UserLoginDTO dto);
		Task<Result> RequestSmsCode(RequestSmsCodeDTO dto);
	}
}
