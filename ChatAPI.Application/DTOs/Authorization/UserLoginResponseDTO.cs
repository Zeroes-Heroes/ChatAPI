using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChatAPI.Application.DTOs.Authorization
{
    public record UserLoginResponseDTO(string? UserName, TokensDTO? Tokens);
}
