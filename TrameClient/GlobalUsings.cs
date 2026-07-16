// Global type aliases for consolidated models.
// The actual types now live in TrameCommon.Models (TrameCommon project).
// These aliases ensure existing code using unqualified type names continues to work.

global using TrameRequest = TrameCommon.Models.TrameRequest;
global using TrameResponse = TrameCommon.Models.TrameResponse;
global using TrameParameter = TrameCommon.Models.TrameParameter;
global using TrameMultiRequest = TrameCommon.Models.TrameMultiRequest;
global using ExecutionMode = TrameCommon.Models.ExecutionMode;
global using TrameError = TrameCommon.Models.TrameError;
global using TrameException = TrameCommon.Exceptions.TrameException;