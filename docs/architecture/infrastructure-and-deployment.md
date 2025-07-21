# Infrastructure and Deployment
Deployment Strategy: The application will be packaged as a modern MSIX installer.

Automation: A GitHub Actions pipeline will be configured to automatically build and package the MSIX file for each new release.

Rollback Strategy: Users can uninstall a new version and reinstall a previous version, which will remain available for download.
