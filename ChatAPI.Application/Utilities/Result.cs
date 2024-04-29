using System.Net;

namespace ChatAPI.Application.Utilities
{
    public record Result<T>(bool IsSuccess, T Data, string Error, int StatusCode)
    {   
        public static Result<T> Success(T data) =>
            new(true, data, null!, (int)HttpStatusCode.OK);

        public static Result<T> Failure(string error, int statusCode = (int)HttpStatusCode.BadRequest) =>
            new(false, default!, error, statusCode);
    }

	public record Result(bool IsSuccess, string Error, int StatusCode)
	{
		public static Result Success() =>
			new(true, null!, (int)HttpStatusCode.NoContent);

		public static Result Failure(string error, int statusCode = (int)HttpStatusCode.BadRequest) =>
			new(false, error, statusCode);
	}
}
