// Global type aliases for consolidated models.
// The actual types now live in SleipnirCommon.Models (SleipnirCommon project).
// These aliases ensure existing code using unqualified type names continues to work.

global using SleipnirRequest = SleipnirCommon.Models.SleipnirRequest;
global using SleipnirResponse = SleipnirCommon.Models.SleipnirResponse;
global using SleipnirParameter = SleipnirCommon.Models.SleipnirParameter;
global using SleipnirMultiRequest = SleipnirCommon.Models.SleipnirMultiRequest;
global using ExecutionMode = SleipnirCommon.Models.ExecutionMode;
global using SleipnirError = SleipnirCommon.Models.SleipnirError;
global using SleipnirException = SleipnirCommon.Exceptions.SleipnirException;