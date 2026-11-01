namespace Shared.BlobStoring;

public interface IBlobContainerFactory
{
    IBlobContainer Create(string name);

    IBlobContainer<TContainer> Create<TContainer>()
        where TContainer : class;
}
