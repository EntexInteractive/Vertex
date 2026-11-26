import {createHashRouter, Navigate} from 'react-router'
import {AppLayout} from './AppLayout'

// Hash routes (#/history) work in both hosts without server-side fallbacks: the desktop app's local server and
// Vertex.Server, which serves the same build from its wwwroot. Tasks, Bugs and Graph are previews with sample data;
// they're reachable from the command palette, not the rail.
export const router = createHashRouter([
    {
        path: '/account',
        lazy: async () => ({Component: (await import('../features/auth/AccountPage')).AccountPage}),
    },
    {
        path: '/',
        Component: AppLayout,
        children: [
            {index: true, element: <Navigate to="/changes" replace/>},
            {path: 'changes', lazy: async () => ({Component: (await import('../features/changes/ChangesPage')).ChangesPage})},
            {path: 'history', lazy: async () => ({Component: (await import('../features/history/HistoryPage')).HistoryPage})},
            {path: 'branches', lazy: async () => ({Component: (await import('../features/branches/BranchesPage')).BranchesPage})},
            {path: 'locks', lazy: async () => ({Component: (await import('../features/locks/LocksPage')).LocksPage})},
            {path: 'tasks', lazy: async () => ({Component: (await import('../features/tasks/TasksPage')).TasksPage})},
            {path: 'bugs', lazy: async () => ({Component: (await import('../features/bugs/BugsPage')).BugsPage})},
            {path: 'graph', lazy: async () => ({Component: (await import('../features/graph/GraphPage')).GraphPage})},
            {path: 'workspace', element: <Navigate to="/changes" replace/>},
            {path: '*', element: <Navigate to="/changes" replace/>},
        ],
    },
])
