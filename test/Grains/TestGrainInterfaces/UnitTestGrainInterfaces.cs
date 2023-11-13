namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IA")]
    public interface IA : IGrainWithIntegerKey
    {
        [Alias("A1Method")]
        Task<string> A1Method();
        [Alias("A2Method")]
        Task<string> A2Method();
        [Alias("A3Method")]
        Task<string> A3Method();
    }

    [Alias("UnitTests.GrainInterfaces.IB")]
    public interface IB : IGrainWithIntegerKey
    {
        [Alias("B1Method")]
        Task<string> B1Method();
        [Alias("B2Method")]
        Task<string> B2Method();
        [Alias("B3Method")]
        Task<string> B3Method();
    }

    [Alias("UnitTests.GrainInterfaces.IC")]
    public interface IC : IA, IB
    {
        [Alias("C1Method")]
        Task<string> C1Method();
        [Alias("C2Method")]
        Task<string> C2Method();
        [Alias("C3Method")]
        Task<string> C3Method();
    }

    [Alias("UnitTests.GrainInterfaces.ID")]
    public interface ID : IC
    {
        [Alias("D1Method")]
        Task<string> D1Method();
        [Alias("D2Method")]
        Task<string> D2Method();
        [Alias("D3Method")]
        Task<string> D3Method();
    }

    [Alias("UnitTests.GrainInterfaces.IE")]
    public interface IE : IGrainWithIntegerKey
    {
        [Alias("E1Method")]
        Task<string> E1Method();
        [Alias("E2Method")]
        Task<string> E2Method();
        [Alias("E3Method")]
        Task<string> E3Method();
    }

    [Alias("UnitTests.GrainInterfaces.IF")]
    public interface IF : ID, IE
    {
        [Alias("F1Method")]
        Task<string> F1Method();
        [Alias("F2Method")]
        Task<string> F2Method();
        [Alias("F3Method")]
        Task<string> F3Method();
    }

    [Alias("UnitTests.GrainInterfaces.IG")]
    public interface IG : IGrainWithIntegerKey
    {
        [Alias("AmbiguousMethod")]
        Task<string> AmbiguousMethod();
    }

    [Alias("UnitTests.GrainInterfaces.IH")]
    public interface IH : IGrainWithIntegerKey
    {
        [Alias("H1Method")]
        Task<string> H1Method();
        [Alias("H2Method")]
        Task<string> H2Method();
        [Alias("H3Method")]
        Task<string> H3Method();
    }

    [Alias("UnitTests.GrainInterfaces.IServiceType")]
    public interface IServiceType : IF
    {
        [Alias("ServiceTypeMethod1")]
        Task<string> ServiceTypeMethod1();
        [Alias("ServiceTypeMethod2")]
        Task<string> ServiceTypeMethod2();
        [Alias("ServiceTypeMethod3")]
        Task<string> ServiceTypeMethod3();
    }

    [Alias("UnitTests.GrainInterfaces.IDerivedServiceType")]
    public interface IDerivedServiceType : IServiceType, IH
    {
        [Alias("DerivedServiceTypeMethod1")]
        Task<string> DerivedServiceTypeMethod1();
    }
}
