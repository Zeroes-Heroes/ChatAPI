using System.Net;

namespace ChatAPI.Application.Utilities
{
    public record Result<T>(bool IsSuccess, T Data, string Error, int StatusCode)
    {   
        public static Result<T> Success(T data, HttpStatusCode code = HttpStatusCode.OK) =>
            new(true, data, null!, (int)code);

        public static Result<T> Failure(string error, HttpStatusCode code = HttpStatusCode.BadRequest) =>
            new(false, default!, error, (int)code);
    }

	public record Result(bool IsSuccess, string Error, int StatusCode)
	{
		public static Result Success(HttpStatusCode code = HttpStatusCode.NoContent) =>
			new(true, null!, (int)code);

		public static Result Failure(string error, HttpStatusCode code = HttpStatusCode.BadRequest) =>
			new(false, error, (int)code);
	}
}
