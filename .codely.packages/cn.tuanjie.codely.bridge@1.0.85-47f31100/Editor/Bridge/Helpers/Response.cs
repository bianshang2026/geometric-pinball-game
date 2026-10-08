using System;
using System.Collections.Generic;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Provides static methods for creating standardized success and error response objects.
    /// Ensures consistent JSON structure for communication back to the Codely client.
    ///
    /// Response format: { success, message, data? }
    ///
    /// There is no pending/op_id/poll_interval variant: a tool call returns only when its work is
    /// finished, so there is nothing for a client to poll or correlate.
    ///
    /// Responses carry only the tool's own result. Editor state is an explicit
    /// read via manage_editor.get_state, not an ambient field on every
    /// response.
    /// </summary>
    public static class Response
    {
        /// <summary>
        /// Creates a standardized success response object.
        /// </summary>
        /// <param name="message">A message describing the successful operation.</param>
        /// <param name="data">Optional additional data to include in the response.</param>
        /// <returns>An object representing the success response.</returns>
        public static object Success(string message, object data = null)
        {
            var response = new Dictionary<string, object>
            {
                { "success", true },
                { "message", message }
            };

            if (data != null)
            {
                response["data"] = data;
            }

            return response;
        }

        /// <summary>
        /// Creates a standardized error response object.
        /// </summary>
        /// <param name="errorCodeOrMessage">A message describing the error.</param>
        /// <param name="data">Optional additional data (e.g., error details) to include.</param>
        /// <returns>An object representing the error response.</returns>
        public static object Error(string errorCodeOrMessage, object data = null)
        {
            var response = new Dictionary<string, object>
            {
                { "success", false },
                { "message", errorCodeOrMessage },
                { "code", errorCodeOrMessage },
                { "error", errorCodeOrMessage }
            };

            if (data != null)
            {
                response["data"] = data;
            }

            return response;
        }

        /// <summary>
        /// Creates an error response whose <c>code</c> is a stable token clients can branch on,
        /// separate from the human-readable message. <see cref="Error"/> copies its single argument
        /// into all three of <c>code</c>, <c>message</c> and <c>error</c>, which is fine for a bare
        /// message but destroys the token as soon as the message says anything more.
        ///
        /// Carries no <c>data</c>: an asynchronous response is rendered as a success by the client
        /// whenever it has a payload, so a failure must be message-only.
        /// </summary>
        public static object ErrorWithCode(string code, string message)
            => new Dictionary<string, object>
            {
                { "success", false },
                { "message", message },
                { "code", code },
                { "error", code },
            };
    }
}
