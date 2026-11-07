// Copyright 2026 Entex Interactive

namespace Vertex.Core.Security
{
    public class UnauthorizedException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UnauthorizedException"/> class.
        /// </summary>
        public UnauthorizedException() : base("This instance is not authorized to access this.") { }

        /// <summary>
        /// Initializes a new instance of the <see cref="UnauthorizedException"/> class with the name of the parameter that causes this exception.
        /// </summary>
        public UnauthorizedException(string paramName) : base(paramName) { }


        /// <summary>
        /// Throws an <see cref="UnauthorizedException"/> if the current instance is the server.
        /// </summary>
        public static void ThrowIfServer()
        {
            if (Authority.IsServer()) throw new UnauthorizedException("Server access denied");
        }

        /// <summary>
        /// Throws an <see cref="UnauthorizedException"/> if the current instance is the client.
        /// </summary>
        public static void ThrowIfClient()
        {
            if (!Authority.IsServer()) throw new UnauthorizedException("Client access denied");
        }
    }
}