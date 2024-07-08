using Services.User.Models;
using Services.Utilities;

namespace Services.User.Interface;

public interface IUserService
{
	Task<Result> Register(UserRegisterDTO dto);
	Task<Result<SecretLoginCodeDTO>> VerifySmsCode(VerifySmsCodeDTO code);
	Task<Result<UserLoginResponseDTO>> Login(UserLoginDTO dto);
	Task<Result> RequestSmsCode(RequestSmsCodeDTO dto);
}
