import {RouterProvider} from 'react-router'
import {router} from './routes'
import {SessionProvider} from './SessionProvider'

export default function App() {
    return (
        <SessionProvider>
            <RouterProvider router={router}/>
        </SessionProvider>
    )
}
