import {useState} from 'react'
import {callBackend, hasPhotinoBackend} from './backend'
import heroImg from './assets/hero.png'
import reactLogo from './assets/vertex-full.svg'
import viteLogo from './assets/vite.svg'
import './App.css'
import {Sidebar} from "./components/Sidebar.tsx";

export default function App() {
    const [currentPage, setCurrentPage] = useState("home");

    return (
        <div className="desktop-app">
            <div className="app-body">
                <Sidebar currentPage={currentPage} onPageChange={setCurrentPage} />
            </div>
        </div>
    )
}
