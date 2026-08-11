using System;

namespace SleipnirCommon.Attribute
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class SleipnirExampleAttribute : System.Attribute
    {
        public string ExampleJson { get; }

        public SleipnirExampleAttribute(string exampleJson)
        {
            ExampleJson = exampleJson;
        }
    }
}
