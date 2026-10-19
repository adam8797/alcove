using Alcove.Wpf.Internal;
using Alcove.Wpf.Tests.Conventions;
using Alcove.Wpf.Tests.Conventions.ViewModels;

namespace Alcove.Wpf.Tests
{
    public sealed class ViewConventionsTests
    {
        [Fact]
        public void AViewModel_IsLookedForInItsNamespace_ThenInTheViewsNamespace()
        {
            Assert.Equal(["App.ViewModels.FooView", "App.Views.FooView"], ViewConventions.CandidateViewNames("App.ViewModels.FooViewModel"));
            Assert.Equal(["App.FooView"], ViewConventions.CandidateViewNames("App.FooViewModel"));
            Assert.Equal(["FooView"], ViewConventions.CandidateViewNames("FooViewModel"));
        }

        [Theory]
        [InlineData("App.Foo")]
        [InlineData("App.ViewModel")]
        [InlineData("App.FooViewModels")]
        public void ATypeNotNamedLikeAViewModel_HasNoView(string name) => Assert.Empty(ViewConventions.CandidateViewNames(name));

        [Fact]
        public void Pairs_AreFoundInTheSameNamespaceAndInViews_AndOnlyForConcretePublicViewModels()
        {
            var pairs = ViewConventions.FindPairs(typeof(ViewConventionsTests).Assembly.GetTypes(), t => t.Name.EndsWith("View", StringComparison.Ordinal));

            Assert.Equal(
                [(typeof(LocalViewModel), typeof(LocalView)), (typeof(ElsewhereViewModel), typeof(Conventions.Views.ElsewhereView))],
                pairs.Where(p => p.ViewModel.Namespace!.StartsWith("Alcove.Wpf.Tests.Conventions", StringComparison.Ordinal)));
        }

        [Fact]
        public void AViewThePredicateRejects_IsNoView()
        {
            var pairs = ViewConventions.FindPairs([typeof(LocalViewModel), typeof(LocalView)], _ => false);

            Assert.Empty(pairs);
        }
    }
}

namespace Alcove.Wpf.Tests.Conventions
{
    public sealed class LocalViewModel;

    public sealed class LocalView;

    public abstract class AbstractViewModel;

    public sealed class AbstractView;

    public sealed class LonelyViewModel;
}

namespace Alcove.Wpf.Tests.Conventions.ViewModels
{
    public sealed class ElsewhereViewModel;

    internal sealed class HiddenViewModel;
}

namespace Alcove.Wpf.Tests.Conventions.Views
{
    public sealed class ElsewhereView;

    public sealed class HiddenView;
}
