namespace TrameCore.Attributes
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class TrameMethodAttribute : System.Attribute
    {
        private readonly string _name;

        public TrameMethodAttribute(string name)
        {
            _name = name;
        }

        public string Name => _name;
    }
}
