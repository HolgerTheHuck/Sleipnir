using TrameCommon.Attribute;
using TrameCore.Attributes;

namespace Trame.Api
{
    [TrameController("TestService")]
    public class TestService
    {
        [TrameMethod("GetAdresse")]
        public AdresseX? GetAdresse(int id, string greet, CancellationToken ct)
        {
            switch (id)
            {
                case 1:
                    return new AdresseX()
                    {
                        Id = 1,
                        Name = "A",
                        Age = 1,
                        Greet = greet
                    };
                case 2:
                    return new AdresseX()
                    {
                        Id = 2,
                        Name = "B",
                        Age = 2,
                        Greet = greet
                    };
            }

            return null;
        }

        [TrameMethod("GetAdresses")]
        public List<AdresseX?> GetAdresses(CancellationToken ct)
        {
            return new List<AdresseX?>()
            {
                GetAdresse(1,"", ct), GetAdresse(2,"", ct)
            };
        }

        [TrameMethod("AddAdresse")]
        public Task<bool> AddAdresse(AdresseX? a, int r)
        {
            if (a == null)
                return Task.FromResult(false);

            return Task.FromResult(true);
        }


        [TrameMethod("GetAdresseParallel")]
        public async Task<AdresseX?> GetAdresseParallel(int id, string greet, CancellationToken ct)
        {
            await Task.Delay(1000, ct);
            switch (id)
            {
                case 1:
                    return new AdresseX()
                    {
                        Id = 1,
                        Name = "A",
                        Age = 1,
                        Greet = greet
                    };
                case 2:
                    return new AdresseX()
                    {
                        Id = 2,
                        Name = "B",
                        Age = 2,
                        Greet = greet
                    };
            }


            return null;
        }
    }

    [TrameExample("{\"Id\":1,\"Name\":\"Holger\",\"Age\":1,\"Greet\":\"Hallo\",\"Contace\":{\"Id\":10,\"Name\":\"Support\"}}")]
    [TrameDocumentation("Diese Klasse repräsentiert eine Adresse mit zugehörigen Kontaktdaten.")]
    // Kein [TrameDataContract] nötig — wird per Signatur-Inferenz expandiert (Weg C),
    // da AdresseX in derselben Assembly wie TestService liegt und in Methodensignaturen auftaucht.
    public class AdresseX
    {
        public int Id { get; set; }
        public string Name { get; set; } = String.Empty;
        public int Age { get; set; }
        public string Greet { get; set; } = String.Empty;

        public Contact? Contace { get; set; }
    }

    public class Contact
    {
        public int Id { get; set; }
        public string Name { get; set; } = String.Empty;
    }

}
