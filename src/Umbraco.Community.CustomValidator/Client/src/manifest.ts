import { UMB_WORKSPACE_CONDITION_ALIAS } from "@umbraco-cms/backoffice/workspace";
import { UMB_DOCUMENT_ENTITY_TYPE } from "@umbraco-cms/backoffice/document";
import { umbExtensionsRegistry } from "@umbraco-cms/backoffice/extension-registry";

export const manifests = [
    {
        type: "workspaceContext",
        alias: "CustomValidator.WorkspaceContext.Validation",
        name: "Validation Workspace Context",
        api: () => import("./contexts/validation-workspace-context.js"),
        conditions: [
            {
                alias: UMB_WORKSPACE_CONDITION_ALIAS,
                match: "Umb.Workspace.Document",
            }
        ],
    },
    {
        type: "workspaceView",
        alias: "CustomValidator.WorkspaceView.Validation",
        name: "Validation Workspace View",
        element: () => import("./views/validation-view.element.js"),
        weight: 10,
        meta: {
            label: "Validation",
            pathname: "validation",
            icon: "icon-alert",
        },
        conditions: [
            {
                alias: UMB_WORKSPACE_CONDITION_ALIAS,
                match: "Umb.Workspace.Document",
            }
        ],
    },
    {
        type: "entitySign",
        kind: "icon",
        alias: "CustomValidator.EntitySign.ValidationError",
        name: "Has Validation Error Entity Sign",
        forEntityTypes: [UMB_DOCUMENT_ENTITY_TYPE],
        forEntityFlags: ["CustomValidator.ValidationErrorsFlag"], 
        weight: 1000,
        meta: {
            iconName: "icon-application-error",
            label: "Validation Error(s)",
            iconColorAlias: "red",
        }
    }
];

export const onInit = () => {
    manifests.forEach((manifest) => umbExtensionsRegistry.register(manifest));
};
