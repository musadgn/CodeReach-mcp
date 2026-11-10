namespace Shop.Domain;

public readonly record struct Money(decimal Amount)
{
    public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount);

    public Money Scale(decimal factor) => new(Amount * factor);
}
