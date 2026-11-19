// Made-up data for the screens whose features aren't built yet (Tasks, Bugs, Graph, messages, storage and the
// workspace change list). Pages that use it pass `preview` to ViewFrame so users can tell. Delete entries as the
// real features land; nothing else should import from here.
import type {Person} from '../components/Avatar'

export const people: Person[] = [
    {name: 'Alex Lee', initials: 'AL', color: '#c5d8ff'},
    {name: 'Maya Kim', initials: 'MK', color: '#dfcaff'},
    {name: 'Jon Bell', initials: 'JB', color: '#bdebd7'},
]

export type ChangeStatus = 'modified' | 'added' | 'deleted'

export const statusLetter: Record<ChangeStatus, string> = {modified: 'M', added: 'A', deleted: 'D'}

export type ChangedFile = {path: string; status: ChangeStatus; add: number; del: number}

export const changedFiles: ChangedFile[] = [
    {path: 'Source/Editor/EditorPanel.cpp', status: 'modified', add: 32, del: 8},
    {path: 'Source/Editor/ProjectTree.cpp', status: 'modified', add: 18, del: 4},
    {path: 'Source/Workspace/WorkspaceState.h', status: 'added', add: 76, del: 0},
    {path: 'Content/Maps/Arena.umap', status: 'modified', add: 0, del: 0},
    {path: 'Content/Legacy/OldHud.uasset', status: 'deleted', add: 0, del: 0},
    {path: 'Config/DefaultGame.ini', status: 'modified', add: 2, del: 2},
]

/** Old line, new line, text. A leading + or - marks an added or deleted line. */
export const sampleDiff: [string, string, string][] = [
    ['', '18', '  struct FEditorPanelArgs'],
    ['', '19', '  {'],
    ['20', '20', '      FString DocumentId;'],
    ['21', '', '-     bool bShowToolbar = true;'],
    ['', '21', '+     FToolbarConfig Toolbar;'],
    ['22', '22', '  };'],
    ['23', '23', ''],
    ['24', '24', '  void SEditorPanel::Construct(const FEditorPanelArgs& Args)'],
    ['25', '25', '  {'],
    ['', '26', '+     Toolbar = Args.Toolbar;'],
    ['26', '27', '      Workspace = FWorkspace::Get();'],
    ['27', '28', '      Editor = MakeShared<FEditor>(Args.DocumentId);'],
    ['', '29', '+     Editor->OnChanged.AddSP(Workspace, &FWorkspace::TrackChange);'],
    ['28', '30', '  }'],
]

export const tasks = [
    {id: 'VX-142', title: 'Finalize editor keyboard shortcuts', status: 'In progress', assignee: people[1], due: 'Today'},
    {id: 'VX-138', title: 'Document branch protection rules', status: 'Review', assignee: people[2], due: 'Tomorrow'},
    {id: 'VX-131', title: 'Add conflict resolution onboarding', status: 'To do', assignee: people[0], due: 'Jun 14'},
    {id: 'VX-126', title: 'Performance pass on repository tree', status: 'To do', assignee: people[1], due: 'Jun 18'},
    {id: 'VX-119', title: 'Validate large file storage flow', status: 'Blocked', assignee: people[2], due: 'Jun 20'},
]

export const bugs = [
    {id: 'BUG-048', title: 'Repository tree loses selection after refresh', severity: 'High', status: 'Investigating', updated: '2h ago'},
    {id: 'BUG-047', title: 'Diff markers misalign on wrapped lines', severity: 'Medium', status: 'Open', updated: 'Yesterday'},
    {id: 'BUG-041', title: 'Lock dialog fails to show the owner', severity: 'Low', status: 'Fix ready', updated: 'Jun 8'},
    {id: 'BUG-039', title: 'Stale branch badge after a merge', severity: 'Medium', status: 'Verified', updated: 'Jun 6'},
]

export const graphRevisions = [
    {number: 1290, message: 'Refine editor panel architecture', branch: 'feature/editor-redesign', author: people[1], when: '12 min ago'},
    {number: 1288, message: 'Add workspace state', branch: 'feature/editor-redesign', author: people[2], when: '1h ago'},
    {number: 1281, message: 'Merge develop into main', branch: 'develop', author: people[0], when: 'Yesterday'},
    {number: 1274, message: 'Improve project tree keyboard navigation', branch: 'main', author: people[1], when: 'Jun 10'},
    {number: 1270, message: 'Release Vertex Desktop 0.1', branch: 'main', author: people[2], when: 'Jun 8'},
    {number: 1266, message: 'Fix sync race condition', branch: 'fix/sync-conflict', author: people[0], when: 'Jun 7'},
]

export const messages = [
    {author: people[1], when: '4m', text: 'I submitted the editor updates. The new diff view is ready for review.'},
    {author: people[2], when: '18m', text: 'The sync fix passed on the staging server.'},
]

export const storage = {usedGb: 68.4, totalGb: 100, contentGb: 51.2, artifactsGb: 17.2}
