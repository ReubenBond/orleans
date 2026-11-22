// remark configuration for eng/check-docs.ps1: every relative link must name an existing file, and every fragment a
// heading of its target under GitHub's slug rules.
import validateLinks from 'remark-validate-links';

export default { plugins: [[validateLinks, { repository: false }]] };
