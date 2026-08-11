using SleipnirCore.Attributes;

namespace Sleipnir.Model
{
    using System.Collections.Generic;

    // DTOs werden per Signatur-Inferenz in der Discovery expandiert (Weg C) — kein
    // [SleipnirDataContract] mehr nötig, da sie in derselben Assembly wie die Controller liegen.
    public class Customer
    {
        public Customer()
        {
        }
        public Customer(int id, string name)
        : this()
        {
            Id = id;
            Name = name;
        }

        public int Id { get; set; }
        public int OrderId { get; set; }
        public string Name { get; set; } = string.Empty;

        public DateTime Created { get; set; }

        public Guid ResourceId { get; set; }

        public Dictionary<string, int>? Map { get; set; }

        public List<Address> Addresses { get; set; } = new();


    }

    public class Address
    {
        public int Id { get; set; }
        public string Street { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string PostalCode { get; set; } = string.Empty;

        public Address()
        {
        }
        public Address(int id, string street, string city, string postalCode)
        : this()
        {
            Id = id;
            Street = street;
            City = city;
            PostalCode = postalCode;
        }
    }

    public class Order
    {
        public int Id { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public decimal Price { get; set; }

        public Order()
        {
        }
        public Order(int id, string productName, decimal price)
        : this()
        {
            Id = id;
            ProductName = productName;
            Price = price;
        }
    }



}
